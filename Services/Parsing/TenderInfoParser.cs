using System;
using System.Globalization;
using System.Text.RegularExpressions;
using QwenWeb.Models;

namespace QwenWeb.Services.Parsing;

public static class TenderInfoParser
{
    public static ParsedTenderInfo Parse(string? description)
    {
        ParsedTenderInfo result = new ParsedTenderInfo
        {
            RawDescription = description ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(description))
        {
            return result;
        }

        // 1. Очистка от HTML и схлопывание пробелов в одну строку
        string cleanText = Regex.Replace(description, @"<[^>]+>", " ");
        cleanText = Regex.Replace(cleanText, @"&nbsp;", " ");
        cleanText = Regex.Replace(cleanText, @"\s+", " ").Trim();

        // 2. Извлечение текстовых полей (остановка на ... или конце строки)
        result.ObjectName = ExtractByRegex(cleanText, @"Наименование объекта закупки:\s*(.+?)(?:\s*\.{3}|$)");
        result.LawType = ExtractByRegex(cleanText, @"Размещение выполняется по:\s*(.+?)(?:\s*\.{3}|$)");
        result.Customer = ExtractByRegex(cleanText, @"Наименование Заказчика:\s*(.+?)(?:\s*\.{3}|$)");
        result.Stage = ExtractByRegex(cleanText, @"Этап размещения:\s*(.+?)(?:\s*\.{3}|$)");

        // 3. Цена и Валюта (часто идут подряд без разделителя ...)
        Match priceMatch = Regex.Match(cleanText, @"Начальная цена контракта:\s*([\d\s.,]+)\s*Валюта:\s*(\w+)", RegexOptions.IgnoreCase);
        if (priceMatch.Success)
        {
            string priceRaw = priceMatch.Groups[1].Value.Replace(" ", string.Empty).Replace(",", ".");
            if (decimal.TryParse(priceRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price))
            {
                result.InitialPrice = price;
            }
            result.Currency = priceMatch.Groups[2].Value.Trim();
        }
        else
        {
            // Фоллбэк, если структура в RSS изменится
            result.Currency = ExtractByRegex(cleanText, @"Валюта:\s*(.+?)(?:\s*\.{3}|$)");
        }

        // 4. Дефолтная валюта
        if (string.IsNullOrEmpty(result.Currency))
        {
            result.Currency = "RUB";
        }

        return result;
    }

    private static string? ExtractByRegex(string source, string pattern)
    {
        Match match = Regex.Match(source, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1)
        {
            string value = match.Groups[1].Value.Trim();
            if (value.EndsWith("..."))
            {
                value = value.Substring(0, value.Length - 3).Trim();
            }
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }
}