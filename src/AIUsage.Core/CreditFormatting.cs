using System.Globalization;

namespace AIUsage.Core;

public static class CreditFormatting
{
    public static bool IsValid(AccountCredit? credit) => credit is not null &&
        !string.IsNullOrWhiteSpace(credit.Id) && !string.IsNullOrWhiteSpace(credit.Name) &&
        !string.IsNullOrWhiteSpace(credit.Unit) && credit.Id.Length <= 256 && credit.Name.Length <= 256 && credit.Unit.Length <= 64 &&
        credit.Balance is not < 0 && credit.Used is not < 0 && credit.Limit is not < 0;

    public static IReadOnlyList<AccountCredit>? Normalize(IReadOnlyList<AccountCredit>? credits)
        => credits?.Where(IsValid).DistinctBy(credit => credit.Id, StringComparer.Ordinal).Take(128).ToArray();

    public static string Value(AccountCredit credit)
    {
        if (!IsValid(credit)) return "—";
        if (credit.IsEnabled == false) return "꺼짐";
        if (credit.Unlimited) return "무제한";
        if (credit.Balance is { } balance) return $"{Number(balance)} {credit.Unit}";
        if (credit.Used.HasValue || credit.Limit.HasValue)
            return $"{Amount(credit.Used)} / {Amount(credit.Limit)} {credit.Unit}";
        return credit.HasCredits switch { true => "있음 · 잔액 미제공", false => "없음", _ => "—" };
    }

    public static string CompactValue(AccountCredit credit) => Value(credit);

    public static string Detail(AccountCredit credit)
    {
        if (!IsValid(credit)) return "크레딧 정보 미제공";
        List<string> lines = [credit.Name];
        if (credit.IsEnabled == false) lines.Add("사용 안 함");
        if (credit.Unlimited) lines.Add("무제한");
        else if (credit.Balance is { } balance) lines.Add($"잔여: {Number(balance)} {credit.Unit}");
        else if (credit.HasCredits is { } hasCredits) lines.Add(hasCredits ? "사용 가능 · 잔액 미제공" : "사용 가능한 크레딧 없음");
        if (credit.Used.HasValue || credit.Limit.HasValue)
        {
            lines.Add($"사용: {Amount(credit.Used)} {credit.Unit}");
            lines.Add($"한도: {Amount(credit.Limit)} {credit.Unit}");
            lines.Add("사용액과 한도는 충전 잔액이 아닙니다.");
        }
        if (lines.Count == 1) lines.Add("수치 미제공");
        return string.Join("\n", lines);
    }

    private static string Amount(decimal? amount) => amount is { } value ? Number(value) : "—";
    // Preserve provider precision, including tiny nonzero balances; do not round them to zero.
    private static string Number(decimal value) => value.ToString("#,0.############################", CultureInfo.InvariantCulture);
}
