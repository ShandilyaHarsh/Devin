namespace Platform.Tests.Infrastructure;

public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for: " + what);
            }

            await Task.Delay(50);
        }
    }
}
