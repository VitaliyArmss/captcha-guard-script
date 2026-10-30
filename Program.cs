using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Playwright;

var mode = ReadArg(args, "--mode") ?? "manual";
var siteUrl = ReadArg(args, "--url") ?? "http://localhost:5173";
var apiUrl = ReadArg(args, "--api") ?? "http://localhost:5080";

try
{
    switch (mode.ToLowerInvariant())
    {
        case "manual":
            await RunBrowserMode(siteUrl);
            break;

        case "local-auto":
            await RunLocalAutoMode(apiUrl);
            break;

        default:
            Console.Error.WriteLine("Неизвестный режим. Используйте --mode manual или --mode local-auto.");
            Environment.ExitCode = 2;
            break;
    }
}
catch (System.TimeoutException ex)
{
    Console.Error.WriteLine($"Время ожидания истекло: {ex.Message}");
    Environment.ExitCode = 1;
}
catch (PlaywrightException ex)
{
    Console.Error.WriteLine($"Ошибка браузера: {ex.Message}");
    Console.Error.WriteLine("Проверьте, что frontend запущен, а Chromium установлен командой из README.");
    Environment.ExitCode = 1;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Ошибка HTTP: {ex.Message}");
    Console.Error.WriteLine("Проверьте, что backend запущен и доступен по заданному --api адресу.");
    Environment.ExitCode = 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static async Task RunBrowserMode(string siteUrl)
{
    Console.WriteLine("Captcha Guard Client — режим браузера");
    Console.WriteLine($"Сайт: {siteUrl}");
    Console.WriteLine("Если Cloudflare запросит действие, решите CAPTCHA в открывшемся окне.");
    Console.WriteLine("Как только токен будет готов, клиент нажмёт кнопку и прочитает сообщение.\n");

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = false,
        Channel = "chrome",
        Args = new[]
    {
        "--disable-blink-features=AutomationControlled", // Скрывает флаг автоматизации
        "--start-maximized" // Делает окно более "человечным"
    }
    });

    // Укажите путь к папке, где будет храниться профиль
    var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptchaGuardProfile");

    var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
    {
        Headless = false,
        Channel = "chrome",
        Args = new[] { "--disable-blink-features=AutomationControlled" }
    });

    var page = context.Pages.First(); // Берем уже открытую страницу
    await page.GotoAsync(siteUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 });

    var button = page.Locator("#verify-button");
    await button.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

    var timer = Stopwatch.StartNew();
    var isEnabled = false;
    while (timer.Elapsed < TimeSpan.FromMinutes(4))
    {
        if (await button.IsEnabledAsync())
        {
            isEnabled = true;
            break;
        }
        await page.WaitForTimeoutAsync(350);
    }

    if (!isEnabled)
        throw new System.TimeoutException("CAPTCHA не выдала токен за 4 минуты. Проверьте ключи, домен виджета и сообщения на странице.");

    Console.WriteLine("Токен готов. Отправляю его на сервер для проверки…");
    await button.ClickAsync();

    var output = page.Locator("#secret-output");
    try
    {
        await output.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        var secret = await output.InnerTextAsync();
        Console.WriteLine("\nПолучена секретная строка:");
        Console.WriteLine(secret);
    }
    catch (PlaywrightException)
    {
        var status = await page.Locator("#status").InnerTextAsync();
        throw new InvalidOperationException($"Backend не вернул сообщение. Статус страницы: {status}");
    }

    await browser.CloseAsync();
}

static async Task RunLocalAutoMode(string apiUrl)
{
    if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var baseUri)
        || (baseUri.Host != "localhost" && baseUri.Host != "127.0.0.1" && baseUri.Host != "::1"))
    {
        throw new InvalidOperationException("Режим local-auto разрешён только для localhost/127.0.0.1 и предназначен для локальной разработки.");
    }

    var apiKey = Environment.GetEnvironmentVariable("CAPTCHA_LAB_AUTOMATION_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("Сначала задайте переменную окружения CAPTCHA_LAB_AUTOMATION_KEY. См. README.md.");

    using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Add("X-Lab-Automation-Key", apiKey);
    using var response = await http.PostAsync("/api/lab/secret", content: null);
    var payload = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Локальный лабораторный endpoint вернул HTTP {(int)response.StatusCode}. Убедитесь, что backend в Development и Automation:Enabled=true.");
    }

    using var document = JsonDocument.Parse(payload);
    if (!document.RootElement.TryGetProperty("secret", out var secretElement))
        throw new InvalidOperationException("Backend не вернул поле secret.");

    Console.WriteLine("Секретная строка получена в локальном режиме разработки:");
    Console.WriteLine(secretElement.GetString());
    Console.WriteLine("Примечание: этот режим не обходит Cloudflare. Он обращается к отдельному endpoint, доступному только в Development с localhost и отдельным ключом.");
}

static string? ReadArg(string[] arguments, string name)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
            return arguments[i + 1];
    }
    return null;
}