namespace Highway.Assurance.Runner.Models;

public sealed class RunProfile
{
    public string Name { get; set; } = "standard-soak";
    public int TargetRatePerSec { get; set; } = 100;
    public int LeaseSeconds { get; set; } = 15; // D12: 15s shortened lease

    // Phase durations in seconds
    public int SettleSeconds { get; set; } = 15;
    public int GapSeconds { get; set; } = 75;
    public int ArrivalSeconds { get; set; } = 35;
    public int SteadySeconds { get; set; } = 40;
    public int TurbulenceSeconds { get; set; } = 50;
    public int DrainSeconds { get; set; } = 15;
    public int ShutdownSeconds { get; set; } = 10;

    // Relative offsets within turbulence phase (seconds from start of turbulence)
    public int SubscriberGracefulRestartOffsetSeconds { get; set; } = 10;
    public int MailerUngracefulKillOffsetSeconds { get; set; } = 25;

    public static RunProfile CreateDefault() => new();

    public static RunProfile CreateShortened() => new()
    {
        Name = "shortened-ci",
        TargetRatePerSec = 25,
        LeaseSeconds = 3,
        SettleSeconds = 2,
        GapSeconds = 4,
        ArrivalSeconds = 4,
        SteadySeconds = 4,
        TurbulenceSeconds = 8,
        DrainSeconds = 8,
        ShutdownSeconds = 2,
        SubscriberGracefulRestartOffsetSeconds = 2,
        MailerUngracefulKillOffsetSeconds = 4
    };
}
