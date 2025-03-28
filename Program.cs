using System.Xml;
using System.Diagnostics;
using System.Net;
using Timer = System.Timers.Timer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class HistoricalDataNotFoundException : Exception
{
    // #Custom exception for request where historical data was not found

    public HistoricalDataNotFoundException() { }

    public HistoricalDataNotFoundException(string message)
        : base(message) { }

    public HistoricalDataNotFoundException(string message, Exception inner)
        : base(message, inner) { }
}

public static class HttpFetcher
{
    // Creating reusable instance of HttpClient
    private static readonly HttpClient _client = new();

    public static async Task<string> FetchFromURL(string url, string? apiKey = null)
    {
        // #Making a request and returning the answer

        int tries = 0;
        int maxTries = 2;

        while (true)
        {
            // Creating new disposable request message

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);

            // Adding API key to request messages headers if its provided as an argument

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("x-api-key", apiKey);
            }

            // Sending the request and waiting for response 

            HttpResponseMessage response;

            try
            {
                response = await _client.SendAsync(request);

                // Trying again when getting statuscode: too many requests

                if (response.StatusCode == HttpStatusCode.TooManyRequests && tries < maxTries)
                {
                    response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? headerValues);

                    string? retryDelay = headerValues?.FirstOrDefault();

                    if (int.TryParse(retryDelay, out int retryDelayMs))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(retryDelayMs));
                        tries++;
                        continue;
                    }
                }

                // Throwing exception when responses statuscode doesn't indicate success

                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"HTTP pyyntö osoitteeseen: {url} epäonnistui\n{e.Message}");
                throw;
            }
            // reading content from response message into a string

            string result = await response.Content.ReadAsStringAsync();

            // Checking edge case where responses content is empty

            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException("Vastauksen sisältö on tyhjä");
            }

            // Returning the responses content as string

            return result;
        }
    }
}

class GetWindMillPowerData
{
    // Getting API key for requests from environment variable

    static readonly string? APIKey = Environment.GetEnvironmentVariable("OPENDATA_API_KEY");

    class TableState
    {
        public TimeSpan NextUpdate { get; set; } = TimeSpan.Zero;
        public string LastUpdateEnd { get; set; } = "";
        public string SplitRowFormat { get; set; } = "";
        public string SplitRowSeparator { get; set; } = "";
    }

    // Creating client for creating API request and received responses
    /*HttpClient client = new HttpClient();*/

    static async Task<float> GetMaxPower()
    {
        // #Fetching historical data to compute maxPower to use as a reference point for the power gauge

        // Computing the timeframe to get historical data from

        string currentTime = XmlConvert.ToString(DateTime.UtcNow, XmlDateTimeSerializationMode.Utc);
        string monthAgo = XmlConvert.ToString(DateTime.UtcNow.AddMonths(-1), XmlDateTimeSerializationMode.Utc);

        int pageSize = 20000;

        string url = $"https://data.fingrid.fi/api/datasets/181/data?startTime={monthAgo}&endTime={currentTime}&pageSize={pageSize}&sortOrder=asc";

        // Fetching the historical data from data.fingrid.fi

        string sJSON = await HttpFetcher.FetchFromURL(url, APIKey);

        // Parsing the response into JSON object

        JObject jsonObj;

        try
        {
            jsonObj = JObject.Parse(sJSON);
        }
        catch (JsonException e)
        {
            throw new JsonReaderException("JSON Vastauksen jäsentäminen epäonnistui pyytäessä historiallisia tietoja", e);
        }

        // Saving relevant tokens from the JSON object

        JToken? total = jsonObj?["pagination"]?["total"];
        JArray? data = jsonObj?["data"]?.Value<JArray>();

        // Checking if the response JSON object structure is correct

        if (total == null | data == null)
        {
            throw new JsonSerializationException("JSON-rakenne on virheellinen.");
        }

        // Checking whether historical data was found from the timeframe

        int count = total!.Value<int>();

        if (count <= 0 || data![0] == null)
        {
            throw new HistoricalDataNotFoundException("Historiallisia tietoja ei löytynyt enimmäisarvon määrittämiseksi.");
        }

        // if there happens to be data on multiple pages, use only the first pages data

        count = Math.Min(count, pageSize);

        // Looping through data elements (power observations) to compute the maximum power value from the timeframe

        float maxPower = 0;

        // Invariant: i = amount of data elements gone through
        for (int i = 0; i < count; i++)
        {
            // FIX: Bad way to handle count (API response's total not corresponging to amount of item's in data JArray (extreme edge case where API's provider made a mistake)

            float power = data![i]?.Value<float>("value") ?? 0;

            if (power > maxPower)
            {
                maxPower = power;
            }
        }

        return maxPower;
    }

    static async Task<(float, string)> GetLatestData()
    {
        // #Fetching latest windmill power data from data.fingrid.fi

        string url = "https://data.fingrid.fi/api/datasets/181/data/latest";

        string sJSON = await HttpFetcher.FetchFromURL(url, APIKey);

        // Deseralizing API response string into JSON object (not parsing due to need to preserve RFC 3339 format for endTime)

        JObject? jsonObj;

        try
        {
            JsonSerializerSettings settings = new JsonSerializerSettings(); settings.DateParseHandling = DateParseHandling.None;

            jsonObj = JsonConvert.DeserializeObject<JObject>(sJSON, settings);
        }
        catch (JsonReaderException)
        {
            Console.WriteLine("JSON Vastauksen deserialisointi epäonnistui pyytäessä viimeisimpiä tietoja");
            throw;
        }

        // Saving relevant data from the response JSON object

        JToken? endTime = jsonObj?["endTime"];
        JToken? value = jsonObj?["value"];

        // Checking if the response JSON structure is correct

        if (endTime == null || value == null)
        {
            throw new JsonSerializationException("JSON-rakenne on virheellinen.");
        }

        // Saving and returning latest power and update timeframes end

        string lastUpdateEnd = endTime.Value<string>()!;
        float currentPower = value.Value<float>();

        return (currentPower, lastUpdateEnd);
    }

    static async Task PrintAndUpdateTable(float maxPower)
    {
        // #Printing windpower information as table

        TableState state = new();

        string CreateCenteredRow(int rowWidth, string text)
        {
            string centeredRow = $"|{new string(' ', (rowWidth - 2 - text.Length) / 2)}{text}";
            centeredRow += $"{new string(' ', rowWidth - centeredRow.Length - 1)}|";

            return centeredRow;
        }

        async Task PrintTable(bool printFullTable = false)
        {
            if (printFullTable)
            {
                (float currentPower, state.LastUpdateEnd) = await GetLatestData();

                // Updating maxPower if currentPower is bigger than maximum power computed for historical data

                maxPower = Math.Max(maxPower, currentPower);

                // Building the table as dynamic

                int barSteps = 12;
                int barSize = (int)Math.Round(currentPower / maxPower * 12, MidpointRounding.ToZero);
                string indicator = barSize > 0 ? "x" : "";
                string bar = $"{new string('-', barSize > 0 ? barSize - 1 : barSize)}{indicator}{new string(' ', barSteps - barSize)}";
                string barRow = $"| 0 |{bar}| {maxPower} |";
                string barRowSeparator = $"|___|{new string('_', barSteps)}|{new string('_', maxPower.ToString().Length + 2)}|";

                int tableWidth = Math.Max(barRow.Length, 23); // 23 = labelRow.MinLength

                string sDate = $"{DateTime.UtcNow.ToString("dd:MM:yyyy")} (UTC)";
                string dateRow = CreateCenteredRow(tableWidth, sDate);

                string line = new string('_', tableWidth - 2);
                string tableTop = $" {line} ";
                string rowSeparator = $"|{line}|";

                int middleLinePos = (int)Math.Round((float)tableWidth / 2, MidpointRounding.ToZero);

                string splitRowFormat = $"| {{0, -{middleLinePos - 2}}}| {{1, -{tableWidth - (middleLinePos + 3)}}}|";

                string fillerCol1 = new string('_', middleLinePos - 1);
                string fillerCol2 = new string('_', tableWidth - (middleLinePos + 2));
                string splitRowSeparator = $"|{fillerCol1}|{fillerCol2}|";

                string powerRow = String.Format(splitRowFormat, "Teho", currentPower);

                string instructionLine1 = "Paina <Esc> tai";
                string instructionLine2 = "\"q\" poistuaksesi";
                string instructionRow1 = CreateCenteredRow(tableWidth, instructionLine1);
                string instructionRow2 = CreateCenteredRow(tableWidth, instructionLine2);

                // Printing the top part of table

                Console.Clear();
                Console.WriteLine($"{tableTop}\n{dateRow}\n{rowSeparator}\n{barRow}\n{barRowSeparator}\n{powerRow}\n{splitRowSeparator}\n\n\n\n\n{instructionRow1}\n{instructionRow2}\n{rowSeparator}");

                Console.SetCursorPosition(0, Console.CursorTop - 7);

                // Updating state variables so that table bottom dimentions can be computed

                state.SplitRowFormat = splitRowFormat;
                state.SplitRowSeparator = splitRowSeparator;
            }

            // Updating current time and next update time

            DateTimeOffset now = new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero);
            string time = now.ToString("HH:mm:ss");

            Debug.Assert(!string.IsNullOrWhiteSpace(state.LastUpdateEnd), "LastUpdateEnd pitäisi olla määritetty");

            DateTimeOffset prevUpdate = DateTimeOffset.Parse(state.LastUpdateEnd);
            state.NextUpdate = prevUpdate.AddMinutes(3) - now;
            string sNextUpdate = state.NextUpdate.ToString(@"hh\:mm\.ss");

            // Printing bottom part of table, time row

            Debug.Assert(!string.IsNullOrWhiteSpace(state.SplitRowFormat), "SplitRowFormat pitäisi olla määritetty");
            Debug.Assert(!string.IsNullOrWhiteSpace(state.SplitRowSeparator), "SplitRowSeparator pitäisi olla määritetty");

            Console.WriteLine(state.SplitRowFormat, "Aika", time);
            Console.WriteLine(state.SplitRowSeparator);
            Console.WriteLine(state.SplitRowFormat, "Päivitys", sNextUpdate);
            Console.WriteLine(state.SplitRowSeparator);

            Console.SetCursorPosition(0, Console.CursorTop - 4);
        };

        // Background task for checking keystroke from user to terminating the program

        CancellationTokenSource cts = new CancellationTokenSource();

        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                ConsoleKey key = Console.ReadKey(true).Key;

                if (key == ConsoleKey.Escape || key == ConsoleKey.Q)
                {
                    Console.Clear();
                    cts.Cancel();
                }
            }
        });

        // Update and print times every second and update table when update time reaches zero

        var LoopInSecondIntervals = async () =>
        {
            // Polling Loop: exits with input <Esc> or "q"

            while (!cts.Token.IsCancellationRequested)
            {
                // Calculating the one second delay

                DateTime now = DateTime.UtcNow;
                /*DateTime nextSecond = now.AddSeconds(1);*/
                /*TimeSpan delay = nextSecond - now;*/
                DateTime nextIntegralSecond = new DateTime(
                    now.Year, now.Month, now.Day,
                    now.Hour, now.Minute, now.Second
                ).AddSeconds(1);
                TimeSpan delay = nextIntegralSecond - now;

                // Print Table: Full if state.NextUpdate reaches zero, else only update time row

                await PrintTable(state.NextUpdate <= TimeSpan.Zero);

                // Delay the next iteration by second

                await Task.Delay(delay, cts.Token);
            }
        };

        try
        {
            await LoopInSecondIntervals();
        }
        catch (OperationCanceledException) { }
    }

    static async Task Main(string[] args)
    {
        try
        {
            Console.CursorVisible = false;

            await PrintAndUpdateTable(await GetMaxPower());
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Käsittelemätön poikkeus metodissa Main: {e.Message}");
            Console.WriteLine(e.StackTrace);
            Console.ResetColor();
        }
    }
}
