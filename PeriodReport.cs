using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace Kpos;

internal static class PeriodReport
{
    internal static string Escape(string value) => WebUtility.HtmlEncode(value);
    internal static void Open(string html, RegisterFolders folders)
    {
        var directory = Path.Combine(folders.Root, "Records", "Reports");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"Sales-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");
        File.WriteAllText(file, html, Encoding.UTF8);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}

internal sealed partial class RegisterData
{
    private sealed record DepartmentLine(string Month, string Type, long Sales, long? Cost);
    private static string ReportDepartment(string type) => type.ToUpperInvariant() switch
    {
        "WHISKEY" or "LIQUEUR" or "BRANDY" or "COGNAC" or "GIN" or "RUM" or "VODKA" or "TEQUILA" or "RTD" or "LIQUOR MISC" => "Liquor",
        "WINE" or "CHAMPAGNE" or "WINE MISC" => "Wine",
        "BEER" or "BEER MISC" => "Beer",
        var t when t.StartsWith("TOBACCO", StringComparison.Ordinal) => "Tobacco",
        "GROCERY" or "SODA" or "ICE CREAM" or "DRUG/CBD" => "Grocery",
        "SNACK" or "SNACKS" => "Snack",
        "LOTTERY" or "LOTTO SALE" => "Lottery",
        _ => type
    };

    internal string BuildPeriodReport(DateOnly start, DateOnly end, int kind)
    {
        if (end < start || kind is < 0 or > 2) throw new ArgumentException("Invalid report period or report type.");
        using var tx = connection.BeginTransaction(deferred: true);
        using var groups = Command("""
            SELECT substr(s.business_date,1,7), coalesce(l.item_type, 'Unclassified'), sum(l.net_cents),
                   CASE WHEN count(l.cost_cents)=count(*) THEN sum(l.cost_cents) END
            FROM sales s JOIN sale_lines l ON l.sale_id=s.id WHERE s.business_date BETWEEN $from AND $to
            GROUP BY 1,2 ORDER BY 1,2
            """, tx);
        groups.Parameters.AddWithValue("$from", Day(start)); groups.Parameters.AddWithValue("$to", Day(end));
        var lines = new List<DepartmentLine>();
        using (var reader = groups.ExecuteReader())
            while (reader.Read()) lines.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        using var totals = Command("""
            SELECT count(*), coalesce(sum(subtotal_cents-discount_cents-lottery_cents),0), coalesce(sum(lottery_cents),0),
                coalesce(sum(tax_cents),0), coalesce(sum(total_cents),0),
                coalesce(sum(CASE WHEN tender='Cash' THEN total_cents END),0),
                coalesce(sum(CASE WHEN tender='Card' THEN total_cents END),0),
                coalesce(sum(round(100*json_extract(tax_details_json,'$.CigTax'))),0),
                (SELECT coalesce(sum(amount_cents),0) FROM drawer_expenses WHERE business_date BETWEEN $from AND $to),
                (SELECT coalesce(sum(CASE WHEN tax_void=1 THEN net_cents END),0) FROM sale_lines JOIN sales ON sale_id=id WHERE business_date BETWEEN $from AND $to),
                (SELECT coalesce(sum(CASE WHEN tax_void=0 AND taxable=0 THEN net_cents END),0) FROM sale_lines JOIN sales ON sale_id=id WHERE business_date BETWEEN $from AND $to)
            FROM sales WHERE business_date BETWEEN $from AND $to
            """, tx);
        totals.Parameters.AddWithValue("$from",Day(start)); totals.Parameters.AddWithValue("$to",Day(end));
        long[] v;
        using (var row = totals.ExecuteReader()) { row.Read(); v = Enumerable.Range(0,11).Select(i=>Convert.ToInt64(row.GetValue(i))).ToArray(); }
        using var names = Command("SELECT name FROM purchase_business WHERE id=1",tx);
        string company = names.ExecuteScalar() as string ?? "kPOS";
        using var closeDays = Command("SELECT business_date FROM day_closeouts WHERE business_date BETWEEN $from AND $to",tx);
        closeDays.Parameters.AddWithValue("$from",Day(start)); closeDays.Parameters.AddWithValue("$to",Day(end));
        var days = new List<DateOnly>();
        using (var rows = closeDays.ExecuteReader()) while(rows.Read()) days.Add(ParseDay(rows.GetString(0)));
        var closed = days.Select(d => ReadCloseout(d,tx)!).ToArray();
        var amounts = closed.Select(EndOfDayAmounts.FromCloseout).ToArray();
        tx.Commit();

        string E(string s) => PeriodReport.Escape(s);
        string Money(decimal? cents) => cents is null ? "N/A" : (cents.Value / 100).ToString("N2", CultureInfo.GetCultureInfo("en-US"));
        string Percent(decimal? n, decimal d) => n is null || d == 0 ? "N/A" : (n.Value/d).ToString("P1",CultureInfo.GetCultureInfo("en-US"));
        decimal? Cost(IEnumerable<DepartmentLine> rows) => rows.Any(r=>r.Cost is null) ? null : rows.Sum(r=>(decimal)(r.Cost ?? 0));
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>kPOS sales reports</title>
            <style>
            @page{size:letter landscape;margin:.45in}*{box-sizing:border-box}body{color:#111;font:10pt Arial,sans-serif;max-width:11in;margin:24px auto}
            header{position:relative;text-align:center}header small{position:absolute;left:0;top:0}h1{margin:5px}h2{text-align:center;border-bottom:3px double;padding-bottom:6px}
            table{width:100%;border-collapse:collapse;font:10pt 'Courier New',monospace}td,th{text-align:right;padding:4px}th:first-child,td:first-child{text-align:left}
            th{border-bottom:1px solid}thead{display:table-header-group}tr{break-inside:avoid}.types{columns:2;column-gap:8%}.pair{display:flex;justify-content:space-between;break-inside:avoid;font:11pt 'Courier New';padding:3px 0}
            .grand{max-width:5in;margin:auto}.block{break-before:page}.matrix{font-size:8pt}footer{text-align:center;font-style:italic;margin-top:20px}
            @media print{button{display:none}body{margin:0;max-width:none}}button{padding:8px 18px}
            </style><button onclick="window.print()">Print report</button>
            """);
        void Header(string title) => html.Append($"<header><small>{DateTime.Now:g}</small><h1>{E(company)}</h1><h1>{title}</h1><h3>From {start:MMMM d, yyyy} To {end:MMMM d, yyyy}</h3></header>");
        void Pair(string name, string value) => html.Append($"<div class='pair'><span>{E(name)}</span><span>{value}</span></div>");
        if (kind != 1)
        {
            Header("Sales Period Report"); html.Append("<h2>Type Totals</h2><div class='types'>");
            foreach (var g in lines.GroupBy(r=>r.Type).OrderBy(g=>g.Key)) Pair(g.Key, Money(g.Sum(r=>r.Sales)));
            html.Append("</div><section class='block'>"); Header("Sales Period Report"); html.Append("<h2>Grand Totals</h2><div class='grand'>");
            Pair("Nbr. of Sales",v[0].ToString("N0")); Pair("Sales",Money(v[1])); Pair("Lottery",Money(v[2]));
            Pair("Sales Tax",Money(v[3]-v[7])); Pair("Cig Tax",Money(v[7])); Pair("Total",Money(v[4])); html.Append("<hr>");
            decimal adjustments = amounts.Sum(a => a.Adjustments.PaidOnAccount - a.Adjustments.CreditGiven - a.Adjustments.OtherPayouts + a.Adjustments.GiftCertificatesSold + a.Adjustments.NetDeposits);
            Pair("Net Bottle",Money(100*amounts.Sum(a=>a.Adjustments.NetDeposits))); Pair("Payments",Money(100*amounts.Sum(a=>a.Adjustments.PaidOnAccount)));
            Pair("Office Payout",Money(v[8])); Pair("Other Payout",Money(100*amounts.Sum(a=>a.Adjustments.OtherPayouts)));
            Pair("Gift Certs Sold",Money(100*amounts.Sum(a=>a.Adjustments.GiftCertificatesSold))); Pair("Total In",Money(v[4]-v[8]+100*adjustments));
            html.Append("<hr>"); Pair("Cash sales",Money(v[5])); Pair("Credit/Debit sales",Money(v[6]));
            Pair("Cash counted",Money(100*amounts.Sum(a=>a.Tender.Cash))); Pair("Checks",Money(100*amounts.Sum(a=>a.Tender.Checks)));
            Pair("Credit/Debit counted",Money(100*amounts.Sum(a=>a.Tender.CardTotal))); Pair("Gift Certs Used",Money(100*amounts.Sum(a=>a.Tender.GiftCertificates)));
            Pair("Credit Given",Money(100*amounts.Sum(a=>a.Adjustments.CreditGiven))); Pair("Coupons",Money(100*amounts.Sum(a=>a.Tender.Coupons)));
            Pair("Total Bank (closed days)",Money(100*amounts.Sum(a=>a.Tender.Total)));
            Pair("Over/Short (closed days)",closed.Length==0?"N/A":Money(100*closed.Sum(c=>EndOfDayAmounts.FromCloseout(c).Difference(c)))); html.Append("<hr>");
            var merchandise = lines.Where(l=>ReportDepartment(l.Type)!="Lottery").ToList();
            var cost = Cost(merchandise);
            Pair("Sales",Money(v[1])); Pair("Cost",Money(cost)); Pair("Gross Profit",Money(v[1]-cost)+" &nbsp; "+Percent(v[1]-cost,v[1]));
            Pair("Tax Exempt Sales",Money(v[9])); Pair("Untaxed Items",Money(v[10]));
            Pair("Bottle Deposits",Money(100*amounts.Sum(a=>a.Adjustments.Deposits))); Pair("Bottle Returns",Money(100*amounts.Sum(a=>a.Adjustments.Returns)));
            html.Append("</div></section>");
        }
        if (kind != 0)
        {
            if(kind==2) html.Append("<section class='block'>");
            Header("Department Gross Profit");
            var departments = lines.Select(r=>ReportDepartment(r.Type)).Distinct().Order().ToArray();
            // Repeat the date and Total on each band so custom departments cannot run off the printed page.
            foreach (var band in (departments.Length==0 ? new[]{Array.Empty<string>()} : departments.Chunk(10)))
            {
                html.Append("<table class='matrix'><thead><tr><th>Date</th>");
                foreach(string d in band)html.Append($"<th>{E(d)}</th>");
                html.Append("<th>Total</th></tr></thead><tbody>");
                void MatrixRow(string label, IEnumerable<DepartmentLine> source, int metric)
                {
                    var subset=source.ToList(); decimal all=subset.Sum(r=>r.Sales);
                    html.Append($"<tr><td>{E(label)}</td>");
                    foreach(var d in band.Cast<string?>().Append(null))
                    {
                        var cells=d is null?subset:subset.Where(r=>ReportDepartment(r.Type)==d).ToList();
                        decimal sales=cells.Sum(r=>r.Sales);decimal? c=Cost(cells);
                        string text=metric switch {0=>Money(sales),1=>Money(c),2=>Money(sales-c),3=>Percent(sales,all),_=>Percent(sales-c,sales)};
                        html.Append($"<td>{text}</td>");
                    }
                    html.Append("</tr>");
                }
                foreach(var month in lines.GroupBy(r=>r.Month))
                {
                    MatrixRow(DateOnly.ParseExact(month.Key+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture).ToString("MMM yyyy"),month,0);
                    MatrixRow("Cost",month,1); MatrixRow("GP",month,2);
                }
                MatrixRow("Total sales",lines,0); MatrixRow("Cost",lines,1); MatrixRow("GP",lines,2);
                MatrixRow("% Total",lines,3); MatrixRow("% GP",lines,4); html.Append("</tbody></table>");
            }
            if(kind==2)html.Append("</section>");
        }
        return html.Append("<footer>End of Report</footer></html>").ToString();
    }
}
