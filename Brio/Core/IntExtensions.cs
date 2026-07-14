using System.Text;

namespace Brio.Core;

public static class IntExtensions
{
    private static string ToChineseClientActorName(int number)
    {
        // The CN client validates generated PCs with different player-name rules.
        // Keep the native name ASCII-only, without spaces, and at most six bytes so
        // Penumbra.GameData can create a valid player identifier for Glamourer.
        if(number < 0 || number >= 260)
            return string.Empty;

        char prefix = (char)('A' + (number / 10));
        string suffix = (number % 10) switch
        {
            0 => "zero",
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            _ => string.Empty
        };

        string name = $"{prefix}{suffix}";
        return name.Length > 6 ? name[..6] : name;
    }

    public static string ToWords(this int number, string separator = " ")
    {
        string[] ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        string[] tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

        StringBuilder result = new();

        if(number < 100)
        {
            if(number < 20)
            {
                result.Append(ones[number]);
            }
            else
            {
                int tenPart = number / 10;
                int onePart = number % 10;
                result.Append(tens[tenPart]);

                if(onePart > 0)
                {
                    result.Append(separator);
                    result.Append(ones[onePart]);
                }
            }
        }
        else
        {
            result.Append(ones[number / 100]);
            result.Append(separator);
            result.Append("Hundred");

            int remainder = number % 100;
            if(remainder > 0)
            {
                result.Append(separator);
                result.Append(ToWords(remainder, separator));
            }
        }

        return result.ToString();
    }

    public static string ToBrioName(this int i)
    {
        if(ClientRegionHelper.IsChineseClient())
            return ToChineseClientActorName(i);

        string result = ToWords(i, " ");

        if(!result.Contains(' '))
            return "Brio " + result;

        return result;
    }

    public static string ToName(this int i)
    {
        if(ClientRegionHelper.IsChineseClient())
            return ToChineseClientActorName(i);

        return ToWords(i, " ");
    }
    public static string ToName(this ulong i)
    {
        return ((int)i).ToName();
    }
}
