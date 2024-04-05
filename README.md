# StressTester

Simple application to stress test an endpoint.

It uses the `HttpClient` to make x requests to an endpoint in parallel for a total of y requests.

## Usage

Edit the `Program.cs` file to set the settings for the test. The settings are as follows:

```bash
// Settings
const int totalRequests = 5000;
const int concurrentRequests = 100;
const string baseUrl = "http://localhost:5294";
const string url = "/weatherforecast";
```
