namespace Haldus;

public static class HaldusTime
{
    /// <summary>
    /// Şimdiki zamanı mikrosaniye hassasiyetinde verir. PostgreSQL zaman damgalarını mikrosaniyeyle
    /// saklar; daha hassas bir değer yazılırsa aynı istekte dönen değer ile sonradan okunan değer farklı olur.
    /// </summary>
    public static DateTimeOffset UtcNow(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), now.Offset);
    }
}
