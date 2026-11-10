namespace PaxCounterWeb.ViewModels;

public class TrackingViewModel
{
    public string PageTitle { get; set; } = "Tracking Map";
    public List<DeviceOptionViewModel> Devices { get; set; } = new();
    public List<string> SelectedDeviceIds { get; set; } = new();
    public List<string> DefaultDeviceIds { get; set; } = new();
    public bool RestrictToDefaultDevices { get; set; }
    public string AllDevicesLabel { get; set; } = "All devices";
    public string EmptyStateMessage { get; set; } = "No devices available yet.";
    public string GpsDisplayMode { get; set; } = "Decimal";
    public bool ForceGpsConfigured { get; set; }
    public bool HaTrackerConfigured { get; set; }
    public string HaTrackerEntityId { get; set; } = "-";
    public string HaTrackerState { get; set; } = "-";
    public string HaTrackerLastUpdatedLocal { get; set; } = "-";
    public string HaTrackerLastUpdatedUtc { get; set; } = "-";
    public string HaTrackerAge { get; set; } = "-";
    public double? HaTrackerLatitude { get; set; }
    public double? HaTrackerLongitude { get; set; }
    public int? HaTrackerAccuracy { get; set; }
    public bool HaTrackerIsStale { get; set; }
    public List<TrackingHaTrackerCardViewModel> HaTrackers { get; set; } = new();
    public bool GpsAutoModeEnabled { get; set; }
    public string TrackerCurrentMode { get; set; } = "away";
    public bool TrackerModeIsManualOverride { get; set; }
    public int HomeIntervalSeconds { get; set; } = 30;
    public int AwayIntervalSeconds { get; set; } = 5;
    public string AutoModeTrackerEntityId { get; set; } = "-";
    public string AutoModeHomeStatesSummary { get; set; } = "-";
    public int ManualHomeIntervalSeconds { get; set; } = 30;
    public int InitialMinutes { get; set; } = 120;
    public string InitialFromLocal { get; set; } = "";
    public string InitialToLocal { get; set; } = "";
    public int InitialRangeHours { get; set; } = 6;
    public bool InitialUseCustomRange { get; set; }
    public string SelectedGpsPhone { get; set; } = "A52";
    public string ActiveAutoModeTrackerEntityId { get; set; } = "-";
    public string ActiveAutoModeTrackerState { get; set; } = "-";
    public string ActiveAutoModeTrackerAge { get; set; } = "-";
    public int GpsSampleCountInWindow { get; set; }
    public int GpsFilteredSampleCountInWindow { get; set; }
    public int GpsUniqueCoordinateCountInWindow { get; set; }
    public int GpsMovementChangeCountInWindow { get; set; }
    public int GpsHiddenJumpCountInWindow { get; set; }
    public string GpsWindowPrimaryTopic { get; set; } = "-";
    public string GpsLastRawPointSummary { get; set; } = "-";
    public string GpsLastAcceptedPointSummary { get; set; } = "-";
    public string GpsLastHiddenJumpSummary { get; set; } = "-";
    public List<TrackingTransportCardViewModel> TransportCards { get; set; } = new();
}


public class TrackingHaTrackerCardViewModel
{
    public string Label { get; set; } = "Tracker";
    public bool IsPrimary { get; set; }
    public bool IsConfigured { get; set; }
    public string EntityId { get; set; } = "-";
    public string State { get; set; } = "-";
    public string LastUpdatedLocal { get; set; } = "-";
    public string LastUpdatedUtc { get; set; } = "-";
    public string Age { get; set; } = "-";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Accuracy { get; set; }
    public bool IsStale { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TrackingTransportCardViewModel
{
    public string DeviceId { get; set; } = "-";
    public string DisplayName { get; set; } = "-";
    public string LastSeenAge { get; set; } = "-";
    public bool IsStale { get; set; }
    public string PathLabel { get; set; } = "-";
    public string SourceChannel { get; set; } = "-";
    public string WifiSsid { get; set; } = "-";
    public string AckId { get; set; } = "-";
    public string AckAge { get; set; } = "-";
    public string ConsecutiveFailures { get; set; } = "-";
    public int Ble { get; set; }
    public int Wifi { get; set; }
}
