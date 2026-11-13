using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData
{
    public int ResetTrainingDay(DateOnly day, RegisterFolders folders, string recoveryZip, DaySales expectedSales, DayCloseout? expectedCloseout)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (GetDaySales(day, transaction) != expectedSales || ReadCloseout(day, transaction) != expectedCloseout)
            throw new InvalidOperationException("The selected day changed. Reopen the reset window and review its latest totals.");

        // The reserved write lock keeps sales and closeouts from changing while the backup is taken.
        ExportBackup(DatabasePath, recoveryZip);
        string archiveFolder = Path.Combine(folders.DailyArchives, day.ToString("yyyy", CultureInfo.InvariantCulture),
            day.ToString("MM", CultureInfo.InvariantCulture));
        var parked = new List<(string Original, string Recovery)>();
        try
        {
            if (Directory.Exists(archiveFolder))
            {
                string recoveryFolder = Path.Combine(folders.Backups, Path.GetFileNameWithoutExtension(recoveryZip));
                foreach (string file in Directory.GetFiles(archiveFolder, $"{Day(day)}_*.zip"))
                {
                    Directory.CreateDirectory(recoveryFolder);
                    string destination = Path.Combine(recoveryFolder, Path.GetFileName(file));
                    File.Move(file, destination);
                    parked.Add((file, destination));
                }
            }

            using var delete = Command("""
                DELETE FROM sale_lines WHERE sale_id IN (SELECT id FROM sales WHERE business_date = $day);
                DELETE FROM day_closeouts WHERE business_date = $day;
                DELETE FROM sales WHERE business_date = $day;
                SELECT changes();
                """, transaction);
            delete.Parameters.AddWithValue("$day", Day(day));
            int count = checked((int)(long)delete.ExecuteScalar()!);
            transaction.Commit();
            return count;
        }
        catch
        {
            foreach (var (original, recovery) in parked)
                File.Move(recovery, original);
            throw;
        }
    }
}

internal sealed class TrainingResetForm : Form
{
    private readonly DateTimePicker day = new() { Width = 130, Format = DateTimePickerFormat.Short, AccessibleName = "Business date" };
    private readonly TextBox confirmation = new() { Width = 160, AccessibleName = "Type the business date", MaxLength = 10 };
    private readonly Label summary = PosView.Label("", 11);
    private readonly Label instruction = PosView.Label("", 11, FontStyle.Bold);
    private readonly Button reset = PosView.Button("&Reset Test Day…", size: 11);
    private readonly RegisterData data;
    private readonly RegisterFolders folders;
    private DaySales? sales;
    private DayCloseout? closeout;
    private DateOnly Day => DateOnly.FromDateTime(day.Value);
    private string ConfirmationDate => Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public TrainingResetForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        Text = "Reset Test Day";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(640, 318);
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        day.MaxDate = data.CurrentBusinessDate().ToDateTime(TimeOnly.MinValue);
        day.Value = data.FirstOpenDayWithSales(data.CurrentBusinessDate()).ToDateTime(TimeOnly.MinValue);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(15)
        };
        layout.Controls.Add(PosView.Label("Reset Test Day", 22, FontStyle.Bold));
        layout.Controls.Add(PosView.Label("Testing only. This clears sales and closeout for the selected business date.", 11, color: PosView.Error));
        var dateRow = new FlowLayoutPanel { AutoSize = true };
        dateRow.Controls.AddRange([PosView.Label("&Business date", 11, FontStyle.Bold), day]);
        layout.Controls.Add(dateRow);
        layout.Controls.Add(summary);
        layout.Controls.Add(PosView.Label("A recovery backup is saved before the reset.", 11, color: PosView.Muted));
        instruction.Margin = new Padding(3, 13, 3, 3);
        layout.Controls.Add(instruction);
        layout.Controls.Add(confirmation);
        var actions = new FlowLayoutPanel { AutoSize = true };
        var cancel = PosView.Button("Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([reset, cancel]);
        layout.Controls.Add(actions);
        Controls.Add(layout);
        CancelButton = cancel;
        day.ValueChanged += (_, _) => LoadDay();
        confirmation.TextChanged += (_, _) => UpdateButton();
        reset.Click += (_, _) => Reset();
        LoadDay();
    }

    private void LoadDay()
    {
        sales = data.GetDaySales(Day);
        closeout = data.ReadCloseout(Day);
        summary.Text = $"{sales.Count:N0} sales · {Sale.Money(sales.Total)} · {(closeout is null ? "Open day" : "Saved closeout")}";
        instruction.Text = $"Type {ConfirmationDate} to enable reset.";
        confirmation.Clear();
        UpdateButton();
    }

    private void UpdateButton() =>
        reset.Enabled = (sales?.Count > 0 || closeout is not null) && confirmation.Text == ConfirmationDate;

    private void Reset()
    {
        if (!reset.Enabled || sales is null || MessageBox.Show(this,
                $"Clear {sales.Count:N0} sales and the closeout for {ConfirmationDate}? Use this only for test data.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try
        {
            Directory.CreateDirectory(folders.Backups);
            string recovery = Path.Combine(folders.Backups, $"before-test-reset-{ConfirmationDate}-{Guid.NewGuid():N}.zip");
            UseWaitCursor = true;
            int removed = data.ResetTrainingDay(Day, folders, recovery, sales, closeout);
            UseWaitCursor = false;
            MessageBox.Show(this, $"Cleared {removed:N0} test sales.\n\nRecovery backup:\n{recovery}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            UseWaitCursor = false;
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            LoadDay();
        }
    }
}
