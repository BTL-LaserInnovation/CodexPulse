using System.IO;
using System.Text.Json;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int FakeServer()
    {
        var fixture = Environment.GetEnvironmentVariable("CODEX_PULSE_CHECK_UI") == "1";
        var started = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var used = new HashSet<string>();
        var keys = new HashSet<string>();
        var reads = 0;
        var switched = 0;
        using var accountWatcher = fixture ? new Timer(_ =>
        {
            var signal = Environment.GetEnvironmentVariable("CODEX_PULSE_CHECK_SIGNAL");
            if (signal is not null && File.Exists(signal) && Interlocked.Exchange(ref switched, 1) == 0)
                Console.WriteLine("""{"method":"account/updated","params":{"authMode":"chatgpt"}}""");
        }, null, 50, 50) : null;
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (!root.TryGetProperty("id", out var id)) continue;
            if (method == "account/rateLimits/read")
            {
                reads++;
                if (!fixture && reads > 1) continue;
                if (reads == 1) Console.WriteLine("""{"method":"account/updated","params":{"authMode":"chatgpt"}}""");
            }
            if (method == "account/login/start")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt64(), error = new { code = -1, message = "secret-test" } }));
                continue;
            }
            object result;
            if (method == "account/rateLimitResetCredit/consume")
            {
                var parameters = root.GetProperty("params");
                var key = parameters.GetProperty("idempotencyKey").GetString()!;
                var credit = parameters.GetProperty("creditId").GetString()!;
                if ((!fixture && (key != "fake-request" || credit != "fake-credit")) ||
                    (fixture && credit is not ("fixture-earliest" or "fixture-later" or "fixture-no-expiry"))) return 3;
                var repeat = !keys.Add(key);
                if (!repeat)
                {
                    used.Add(credit);
                    if (Environment.GetEnvironmentVariable("CODEX_PULSE_CHECK_LOG") is { } log)
                        File.AppendAllText(log, credit + "\n");
                }
                result = new { outcome = repeat ? "alreadyRedeemed" : "reset" };
            }
            else if (method == "account/rateLimits/read" && fixture)
            {
                if (Environment.GetEnvironmentVariable("CODEX_PULSE_PREVIEW_PROFILE") is { } profile)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt64(), result = CompactPreviewResult(profile, started) }));
                    continue;
                }
                var definitions = new[]
                {
                    new { id = "fixture-later", expires = (long?)started.AddDays(3).ToUnixTimeSeconds(), title = "이름이 아주 긴 테스트 리셋권 · Long credit name" },
                    new { id = "fixture-no-expiry", expires = (long?)null, title = "테스트 · 만료일 없음" },
                    new { id = "fixture-earliest", expires = (long?)started.AddHours(6).ToUnixTimeSeconds(), title = "테스트 · 먼저 만료" }
                };
                result = new
                {
                    rateLimits = new
                    {
                        primary = new { usedPercent = 37, windowDurationMins = 10080, resetsAt = started.AddDays(4).ToUnixTimeSeconds() },
                        secondary = new { usedPercent = 12, windowDurationMins = 300, resetsAt = started.AddHours(4).ToUnixTimeSeconds() }
                    },
                    rateLimitResetCredits = new
                    {
                        availableCount = 5 - used.Count,
                        credits = definitions.Where(c => !used.Contains(c.id)).Select(c => new { c.id, resetType = "codexRateLimits", status = "available", grantedAt = started.AddDays(-1).ToUnixTimeSeconds(), expiresAt = c.expires, c.title, description = "검증용 리셋권입니다. 실제 계정에 영향을 주지 않습니다." }).ToArray()
                    }
                };
            }
            else result = method switch
            {
                "initialize" => new { userAgent = "checks" },
                "account/read" => new { account = new { type = "chatgpt", email = Volatile.Read(ref switched) == 1 ? "other@example.invalid" : "test@example.invalid", planType = "business" } },
                "account/rateLimits/read" => new { rateLimits = new { primary = new { usedPercent = 37, windowDurationMins = 300, resetsAt = 1793750400 } } },
                _ => new { }
            };
            Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt64(), result }));
        }
        return 0;
    }
}
