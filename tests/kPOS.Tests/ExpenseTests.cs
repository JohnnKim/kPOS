using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class ExpenseTests
{
    [TestMethod]
    public void DrawerExpensesAdjustCashAndAreFrozenInTheClosedDay()
    {
        using var data = new RegisterData(":memory:");
        var time = new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.FromHours(-7));
        DateOnly day = DateOnly.FromDateTime(time.DateTime);
        var sale = new Sale();
        sale.AddMisc("Other", 20m, taxable: false);
        SaleTotals totals = sale.GetTotals(data.TaxRate);
        data.RecordSale(Guid.NewGuid(), time, sale, totals, new Payment(PaymentMethod.Cash, 20m));
        var initialCloseout = new DayCloseout(day, time, data.GetDaySales(day), 100m, 107.75m, 0m, 50m, "");

        Guid id = Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => data.RecordExpense(id, time, 12.251m, "Supplies"));
        Assert.ThrowsExactly<ArgumentException>(() => data.RecordExpense(id, time, 12.25m, " "));
        data.RecordExpense(id, time, 12.25m, "  Supplies  ");
        data.RecordExpense(id, time, 12.25m, "Supplies");
        Assert.IsNotNull(data.CloseDay(initialCloseout));
        DaySales sales = data.GetDaySales(day);
        Assert.AreEqual(20m, sales.Total);
        Assert.AreEqual(12.25m, sales.Expenses);
        Assert.IsNull(data.CloseDay(initialCloseout with { Sales = sales }));

        data.RecordExpense(Guid.NewGuid(), time.AddMinutes(10), 5m, "Parking");
        DayCloseout saved = data.ReadCloseout(day)!;
        Assert.AreEqual(107.75m, saved.ExpectedCash);
        Assert.AreEqual(0m, saved.CashDifference);
        Assert.AreEqual(12.25m, saved.Sales.Expenses);
        var expense = data.ReadReport(RecordReport.Expenses, day, day).Rows[0];
        Assert.AreEqual("Supplies", expense["Reason"]);
        Assert.AreEqual(12.25m, Convert.ToDecimal(expense["Amount"], CultureInfo.InvariantCulture));
        var closeout = data.ReadReport(RecordReport.Closeout, day, day).Rows[0];
        Assert.AreEqual(107.75m, Convert.ToDecimal(closeout["Expected cash"], CultureInfo.InvariantCulture));

        DateOnly next = day.AddDays(1);
        Assert.AreEqual(next, data.FirstOpenDayWithActivity(day.AddDays(3)));
        DaySales nextDay = data.GetDaySales(next);
        Assert.AreEqual(0L, nextDay.Count);
        Assert.AreEqual(5m, nextDay.Expenses);
        Assert.IsNull(data.CloseDay(new DayCloseout(next, time, nextDay, 50m, 45m, 0m, 45m, "")));
    }
}
