using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PaxCounterWeb.Data;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

var projectRoot = ResolveProjectRoot();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = projectRoot
});
// Direct GPS credentials live outside the repository so phones can be rotated
// independently without exposing keys in source control.
builder.Configuration.AddJsonFile("appsettings.directgps.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.homewifi.local.json", optional: true, reloadOnChange: true);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5283");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

var dataProtectionPath = Path.Combine(AppContext.BaseDirectory, "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionPath);
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

var sqliteBuilder = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"))
{
    DefaultTimeout = 15,
    Pooling = true,
    Mode = SqliteOpenMode.ReadWriteCreate
};

if (!string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) && !Path.IsPathRooted(sqliteBuilder.DataSource))
{
    sqliteBuilder.DataSource = Path.Combine(builder.Environment.ContentRootPath, sqliteBuilder.DataSource);
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteBuilder.ToString(), sqlite => sqlite.CommandTimeout(15)));

builder.Services.AddHttpClient<TtnDownlinkService>();
builder.Services.AddScoped<PhoneGpsService>();
builder.Services.AddSingleton<PhoneGpsControlStore>();
builder.Services.AddSingleton<HomeWifiSettingsService>();
builder.Services.AddHttpClient<FootfallRelayService>();
builder.Services.AddScoped<PaxSimulatorService>();
builder.Services.AddSingleton<WebhookLogStore>();
builder.Services.AddSingleton<TrackStore>();
builder.Services.AddScoped<GpsDisplaySettingsService>();
builder.Services.AddScoped<UiFeatureSettingsService>();
builder.Services.AddScoped<TrackerHomeModeSettingsService>();
builder.Services.AddSingleton<TrackerModeOverrideService>();
builder.Services.AddScoped<DeviceCommandService>();
builder.Services.AddScoped<DirectGpsIngestService>();

// Phone auto-mode is evaluated on authenticated Direct GPS polls.
if (builder.Configuration.GetValue("BackgroundServices:MqttSubscriberEnabled", false))
{
    builder.Services.AddHostedService<MqttSubscriberService>();
}

if (builder.Configuration.GetValue("BackgroundServices:PendingCommandDispatcherEnabled", true))
{
    builder.Services.AddHostedService<PendingCommandDispatcherService>();
}

var app = builder.Build();

// Ensure known devices exist so they are visible in UI before first uplink arrives.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var trackStore = scope.ServiceProvider.GetRequiredService<TrackStore>();
    EnsureSqlitePragmas(db);
    EnsurePaxSamplesSourceColumn(db);
    EnsureGpsSamplesTable(db);
    EnsurePhoneGpsHeartbeatsTable(db);
    EnsureTrackerPositionsTable(db);
    EnsureFootfallTables(db);
    EnsurePendingDeviceCommandsTable(db);
    EnsureDevicesIsHiddenColumn(db);

    var knownDevices = new[]
    {
        new Device
        {
            DeviceId = "paxcounter-heltec1",
            DeviceUid = "13B66DCC3F47B2BC",
            Name = "paxcounter-heltec1"
        },
        new Device
        {
            DeviceId = "paxcounter-heltec2",
            DeviceUid = "2C8F4A91D36BE7F2",
            Name = "paxcounter-heltec2"
        }
    };

    foreach (var known in knownDevices)
    {
        var existing = db.Devices.FirstOrDefault(d =>
            d.DeviceId == known.DeviceId || d.DeviceUid == known.DeviceUid);

        if (existing == null)
        {
            db.Devices.Add(known);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(existing.DeviceId))
            {
                existing.DeviceId = known.DeviceId;
            }

            if (string.IsNullOrWhiteSpace(existing.DeviceUid))
            {
                existing.DeviceUid = known.DeviceUid;
            }

            if (string.IsNullOrWhiteSpace(existing.Name))
            {
                existing.Name = known.Name;
            }
        }
    }

    db.SaveChanges();
    trackStore.SetKnownDevices(db.Devices.Where(d => !d.IsHidden).Select(d => d.DeviceId).ToList());
}
// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseRouting();
app.UseStaticFiles();

app.UseAuthorization();


app.MapControllers();

app.MapGet("/", context =>
{
    context.Response.Redirect("/landing");
    return Task.CompletedTask;
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Tracking}/{action=Index}/{id?}");


app.Run();

static void EnsureSqlitePragmas(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var busyTimeout = conn.CreateCommand();
        busyTimeout.CommandText = "PRAGMA busy_timeout=15000;";
        busyTimeout.ExecuteNonQuery();

        using var journalMode = conn.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=WAL;";
        journalMode.ExecuteNonQuery();

        using var synchronous = conn.CreateCommand();
        synchronous.CommandText = "PRAGMA synchronous=NORMAL;";
        synchronous.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static void EnsurePaxSamplesSourceColumn(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info('PaxSamples');";
        using var reader = check.ExecuteReader();
        var hasSourceColumn = false;
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "SourceChannel", StringComparison.OrdinalIgnoreCase))
            {
                hasSourceColumn = true;
                break;
            }
        }

        if (!hasSourceColumn)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE PaxSamples ADD COLUMN SourceChannel TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static void EnsureGpsSamplesTable(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var create = conn.CreateCommand();
        create.CommandText = @"
CREATE TABLE IF NOT EXISTS GpsSamples (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    Latitude REAL NOT NULL,
    Longitude REAL NOT NULL,
    Accuracy INTEGER NULL,
    SourceTopic TEXT NULL
);";
        create.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static void EnsureDevicesIsHiddenColumn(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info('Devices');";
        using var reader = check.ExecuteReader();
        var hasIsHiddenColumn = false;
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "IsHidden", StringComparison.OrdinalIgnoreCase))
            {
                hasIsHiddenColumn = true;
                break;
            }
        }

        if (!hasIsHiddenColumn)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Devices ADD COLUMN IsHidden INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static string ResolveProjectRoot()
{
    var candidate = new DirectoryInfo(AppContext.BaseDirectory);
    while (candidate is not null)
    {
        if (File.Exists(Path.Combine(candidate.FullName, "PaxCounterWeb.csproj")))
        {
            return candidate.FullName;
        }

        candidate = candidate.Parent;
    }

    return AppContext.BaseDirectory;
}

static void EnsureTrackerPositionsTable(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var create = conn.CreateCommand();
        create.CommandText = @"
CREATE TABLE IF NOT EXISTS TrackerPositions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceId INTEGER NOT NULL,
    TimestampUtc TEXT NOT NULL,
    Latitude REAL NOT NULL,
    Longitude REAL NOT NULL,
    Altitude INTEGER NULL,
    Satellites INTEGER NULL,
    Hdop REAL NULL,
    BatteryVoltageMv INTEGER NULL,
    BatteryPercent REAL NULL,
    SourceChannel TEXT NOT NULL DEFAULT 'LoRa/TTN',
    FOREIGN KEY(DeviceId) REFERENCES Devices(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_TrackerPositions_DeviceId_TimestampUtc
    ON TrackerPositions(DeviceId, TimestampUtc);
";
        create.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static void EnsurePhoneGpsHeartbeatsTable(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var create = conn.CreateCommand();
        create.CommandText = @"
CREATE TABLE IF NOT EXISTS PhoneGpsHeartbeats (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PhoneId TEXT NOT NULL UNIQUE,
    LastReceivedUtc TEXT NOT NULL,
    SampleTimestampUtc TEXT NOT NULL,
    Latitude REAL NOT NULL,
    Longitude REAL NOT NULL,
    Accuracy INTEGER NULL,
    BatteryPercent INTEGER NULL,
    NetworkType TEXT NULL,
    WifiSsid TEXT NULL,
    PendingSamples INTEGER NOT NULL DEFAULT 0,
    AppVersion TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_PhoneGpsHeartbeats_PhoneId
    ON PhoneGpsHeartbeats(PhoneId);";
        create.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static void EnsurePendingDeviceCommandsTable(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var create = conn.CreateCommand();
        create.CommandText = @"
CREATE TABLE IF NOT EXISTS PendingDeviceCommands (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceEntityId INTEGER NOT NULL,
    CommandName TEXT NOT NULL,
    PayloadBase64 TEXT NOT NULL,
    PayloadHex TEXT NOT NULL,
    RequestedSeconds INTEGER NOT NULL,
    RequestedTransport TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    LoRaAttemptedAtUtc TEXT NULL,
    LoRaAccepted INTEGER NOT NULL DEFAULT 0,
    LoRaStatus TEXT NULL,
    ConsumedAtUtc TEXT NULL,
    WifiDispatchCount INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(DeviceEntityId) REFERENCES Devices(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_PendingDeviceCommands_DeviceEntityId_ConsumedAtUtc
    ON PendingDeviceCommands(DeviceEntityId, ConsumedAtUtc);";
        create.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}

static void EnsureFootfallTables(AppDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var shouldClose = conn.State != System.Data.ConnectionState.Open;
    if (shouldClose)
    {
        conn.Open();
    }

    try
    {
        using var create = conn.CreateCommand();
        create.CommandText = @"
CREATE TABLE IF NOT EXISTS FootfallLayouts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Width INTEGER NOT NULL,
    Height INTEGER NOT NULL,
    BackgroundImageUrl TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS FootfallNodes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    LayoutId INTEGER NOT NULL,
    DeviceId TEXT NOT NULL,
    Label TEXT NOT NULL,
    Zone TEXT NOT NULL DEFAULT '',
    X REAL NOT NULL,
    Y REAL NOT NULL,
    AccentColor TEXT NOT NULL DEFAULT '#2563eb',
    RlimHint INTEGER NOT NULL DEFAULT 0,
    IsActive INTEGER NOT NULL DEFAULT 1,
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY(LayoutId) REFERENCES FootfallLayouts(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_FootfallNodes_LayoutId
    ON FootfallNodes(LayoutId);
CREATE INDEX IF NOT EXISTS IX_FootfallNodes_DeviceId
    ON FootfallNodes(DeviceId);
";
        create.ExecuteNonQuery();
    }
    finally
    {
        if (shouldClose)
        {
            conn.Close();
        }
    }
}





