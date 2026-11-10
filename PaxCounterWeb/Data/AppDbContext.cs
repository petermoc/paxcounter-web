namespace PaxCounterWeb.Data
{
    using global::PaxCounterWeb.Models;
    using global::PaxCounterWeb.Models.PaxCounterWeb.Models;
    using Microsoft.EntityFrameworkCore;
    //using PaxCounterWeb.Models;
    using System.Diagnostics.Metrics;

    namespace PaxCounterWeb.Data
    {
        public class AppDbContext : DbContext
        {
            public AppDbContext(DbContextOptions<AppDbContext> options)
                 : base(options) { }

            public DbSet<PaxSample> PaxSamples => Set<PaxSample>();
            public DbSet<RssiSample> RssiSamples => Set<RssiSample>();
            public DbSet<GpsSample> GpsSamples => Set<GpsSample>();
            public DbSet<PhoneGpsHeartbeat> PhoneGpsHeartbeats => Set<PhoneGpsHeartbeat>();
            public DbSet<TrackerPosition> TrackerPositions => Set<TrackerPosition>();
            public DbSet<FootfallLayout> FootfallLayouts => Set<FootfallLayout>();
            public DbSet<FootfallNode> FootfallNodes => Set<FootfallNode>();
            public DbSet<PendingDeviceCommand> PendingDeviceCommands => Set<PendingDeviceCommand>();
            //public DbSet<Device> Devices { get; set; }

            public DbSet<Device> Devices => Set<Device>();

        }
    }

}
