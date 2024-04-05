using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using Serilog;

// Settings
const int totalRequests = 5000;
const int concurrentRequests = 100;
const string baseUrl = "http://localhost:5294";
const string url = "/weatherforecast";

// Logger
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

var statistics = new ConcurrentDictionary<HttpStatusCode, List<TimeSpan>>();
var requestTasks = new ConcurrentQueue<Task<HttpResponseMessage>>();
var semaphore = new SemaphoreSlim(concurrentRequests);

using var client = new HttpClient();
client.BaseAddress = new Uri(baseUrl);
client.Timeout = TimeSpan.FromMinutes(5);

Log.Information("Starting requests");

var stopWatch = new Stopwatch();
stopWatch.Start();

for (var i = 0; i < totalRequests; i++)
{
    // Wait until a slot is available in the semaphore
    await semaphore.WaitAsync();

    // Create a task for each request
    var requestTask = SendRequestAsync(client, requestTasks.Count + 1, totalRequests);
    requestTasks.Enqueue(requestTask);
}

// Wait for all the tasks to complete
await Task.WhenAll(requestTasks);

Log.Information("All requests completed");

stopWatch.Stop();
var ts = stopWatch.Elapsed;

Log.Information("Total time: {Minutes}m {Seconds}s {Milliseconds}ms", ts.Minutes, ts.Seconds, ts.Milliseconds);

Log.Information("Statistics:");

var totalRequestsInStatistics =
    statistics.Values.SelectMany(times => times).Count(); // Should be equal to totalRequests
foreach (var (statusCode, times) in statistics.OrderByDescending(x => x.Value.Count))
{
    var min = times.Min(time => time.TotalMilliseconds);
    var max = times.Max(time => time.TotalMilliseconds);
    var avg = times.Average(time => time.TotalMilliseconds);
    var count = times.Count;
    Log.Information(
        "Status code: {StatusCode} Stats: min {Min:F3}ms, max {Max:F3}ms, avg {Average:F3}ms, count {Count} ({Percent:P} of total requests)",
        statusCode.ToString().PadRight(20), min, max, avg, count, (double) count / totalRequestsInStatistics);
}

return;

async Task<HttpResponseMessage> SendRequestAsync(HttpClient httpClient, int currentRequestCount, int totalRequestCount)
{
    try
    {
        var requestStopWatch = new Stopwatch();
        requestStopWatch.Start();

        var policyWrap = Policy.WrapAsync(Policies.AsyncRetryPolicy(), Policies.AsyncTimeoutPolicy());

        var response =
            await policyWrap.ExecuteAsync(async ct => await httpClient.GetAsync(url, ct), CancellationToken.None);

        requestStopWatch.Stop();
        var requestTs = requestStopWatch.Elapsed;

        Log.Information("Request {Added} / {Total} completed in {Seconds}s {Milliseconds}ms with status {StatusCode}",
            currentRequestCount, totalRequestCount, requestTs.Seconds, requestTs.Milliseconds, response.StatusCode);

        AddToStatistics(response.StatusCode, requestTs);

        return response;
    }
    catch (Exception ex)
    {
        Log.Error("Request failed: {ExMessage}", ex.Message);
        throw;
    }
    finally
    {
        // Release the semaphore slot
        semaphore.Release();
    }
}

void AddToStatistics(HttpStatusCode statusCode, TimeSpan timeSpan)
{
    if (!statistics.TryGetValue(statusCode, out var timeSpans))
    {
        timeSpans = new List<TimeSpan>();
        statistics[statusCode] = timeSpans;
    }

    timeSpans.Add(timeSpan);
}

internal static class Policies
{
    // Retry a specified number of times, using a function to
    // calculate the duration to wait between retries based on
    // the current retry attempt (allows for exponential back-off)
    // In this case will wait for
    //  4 ^ 1 = 4 seconds then
    //  4 ^ 2 = 16 seconds then
    //  4 ^ 3 = 64 seconds then
    //  4 ^ 4 = 256 seconds then
    public static AsyncRetryPolicy AsyncRetryPolicy() =>
        Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(
                4,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(4, retryAttempt)),
                (exception, timeSpan, _) =>
                {
                    Log.Warning(
                        "Request returned: {Message} - retrying with exponential backoff (waiting {TimespanSeconds}s)",
                        exception.Message, timeSpan.Seconds);
                }
            );

    // Timeout and return to the caller after 5 minutes, if the executed delegate has not completed. Optimistic timeout: Delegates should take and honour a CancellationToken.
    public static AsyncTimeoutPolicy AsyncTimeoutPolicy() =>
        Policy.TimeoutAsync(TimeSpan.FromMinutes(5),
            onTimeoutAsync: (_, timespan, _) =>
            {
                Log.Error("Request timed out after {Seconds} seconds",
                    timespan.TotalSeconds);
                return Task.CompletedTask;
            });
}