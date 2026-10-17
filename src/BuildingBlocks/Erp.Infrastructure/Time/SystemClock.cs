using Erp.SharedKernel.Application;

namespace Erp.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    private static readonly TimeZoneInfo Qatar = ResolveQatar();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Qatar).DateTime);

    private static TimeZoneInfo ResolveQatar()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Qatar"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("Asia/Qatar", TimeSpan.FromHours(3), "Qatar", "Qatar"); }
    }
}
