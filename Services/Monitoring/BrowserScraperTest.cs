using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QwenWeb.Services.Monitoring;

public static class BrowserScraperTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("🌐 запуск тестового браузера (chromium)...");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args = new[] { "--disable-blink-features=AutomationControlled" }
        });

        var context = await browser.NewContextAsync(new()
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ViewportSize = new() { Width = 1280, Height = 900 }
        });

        var page = await context.NewPageAsync();
        var regNumbers = new HashSet<string>();
        int pageLimit = 5;
        int currentPage = 1;

        try
        {
            while (currentPage <= pageLimit)
            {
                var today = DateTime.Today.ToString("dd.MM.yyyy");
                var url = $"https://zakupki.gov.ru/epz/order/extendedsearch/results.html?" +
                          $"morphology=on&" +
                          $"search-filter=%D0%94%D0%B0%D1%82%D0%B5+%D1%80%D0%B0%D0%B7%D0%BC%D0%B5%D1%89%D0%B5%D0%BD%D0%B8%D1%8F&" +
                          $"pageNumber={currentPage}&" +
                          $"sortDirection=false&" +
                          $"recordsPerPage=50&" +
                          $"showLotsInfoHidden=false&" +
                          $"sortBy=UPDATE_DATE&" +
                          $"fz44=on&" +
                          $"af=on&" +
                          $"currencyIdGeneral=-1&" +
                          $"publishDateFrom={today}&" +
                          $"selectedSubjects=38000000000";

                Console.WriteLine($"\n📄 загрузка страницы {currentPage}...");
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

                try
                {
                    await page.WaitForSelectorAsync("a[href*='regNumber=']", new() { Timeout = 15000 });
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("⚠️ элементы с номерами закупок не найдены. возможна captcha или пустая выдача.");
                    break;
                }

                var links = await page.QuerySelectorAllAsync("a[href*='regNumber=']");
                foreach (var link in links)
                {
                    var href = await link.GetAttributeAsync("href");
                    if (string.IsNullOrEmpty(href)) continue;

                    var match = Regex.Match(href, @"regNumber=([0-9A-Za-z\-_]+)");
                    if (match.Success)
                    {
                        regNumbers.Add(match.Groups[1].Value);
                    }
                }

                Console.WriteLine($"✅ найдено ссылок на стр.: {links.Count()} | всего уникальных regnumber: {regNumbers.Count}");

                var hasNext = await page.QuerySelectorAsync("a.next-page, .pagination__next, [class*='next']") != null;
                if (!hasNext)
                {
                    Console.WriteLine("🏁 кнопка «далее» отсутствует. это последняя страница.");
                    break;
                }

                currentPage++;
                await Task.Delay(2500);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ошибка во время парсинга: {ex.Message}");
        }
        finally
        {
            await browser.CloseAsync();
        }

        var fileName = $"irkutsk_44fz_{DateTime.Today:yyyy-MM-dd}_regnumbers.txt";
        await File.WriteAllLinesAsync(fileName, regNumbers.OrderBy(x => x));
        Console.WriteLine($"\n🎉 готово. сохранено {regNumbers.Count} уникальных номеров в файл: {fileName}");
    }
}