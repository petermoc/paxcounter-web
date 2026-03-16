using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PaxCounterWeb.Data;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

var sqliteBuilder = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"))
{
    DefaultTimeout = 15,
    Pooling = true,
    Mode = SqliteOpenMode.ReadWriteCreate
};

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteBuilder.ToString(), sqlite => sqlite.CommandTimeout(15)));

builder.Services.AddHttpClient<TtnDownlinkService>();
builder.Services.AddHttpClient<HomeAssistantGpsService>();
builder.Services.AddScoped<PaxSimulatorService>();
builder.Services.AddSingleton<WebhookLogStore>();
builder.Services.AddSingleton<TrackStore>();
builder.Services.AddScoped<GpsDisplaySettingsService>();
builder.Services.AddScoped<DeviceCommandService>();
builder.Services.AddHostedService<MqttSubscriberService>();
builder.Services.AddHostedService<PendingCommandDispatcherService>();
builder.Services.AddHostedService<PendingCommandDispatcherService>();

var app = builder.Build();

// Ensure known devices exist so they are visible in UI before first uplink arrives.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var trackStore = scope.ServiceProvider.GetRequiredService<TrackStore>();
    EnsureSqlitePragmas(db);
    EnsurePaxSamplesSourceColumn(db);
    EnsureGpsSamplesTable(db);
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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


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

