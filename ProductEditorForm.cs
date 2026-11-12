using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class ProductEditorForm : Form
{
    private readonly TextBox code = new() { MaxLength = 80, Dock = DockStyle.Fill, AccessibleName = "Item code" };
    private readonly TextBox description = new() { MaxLength = 120, Dock = DockStyle.Fill, AccessibleName = "Product name" };
    private readonly TextBox size = new() { MaxLength = 40, Dock = DockStyle.Fill, AccessibleName = "Size or pack" };
    private readonly TextBox unitPrice = new() { MaxLength = 10, Dock = DockStyle.Fill, AccessibleName = "Unit price" };
    private readonly TextBox caseQuantity = new() { MaxLength = 4, Dock = DockStyle.Fill, AccessibleName = "Units per case" };
    private readonly TextBox casePrice = new() { MaxLength = 10, Dock = DockStyle.Fill, AccessibleName = "Case price" };
    private readonly CheckBox sellCase = new() { Text = "Sell by the case", AutoSize = true };
    private readonly CheckBox taxable = new() { Text = "Apply sales tax", AutoSize = true, Checked = true };
    private readonly Label feedback = new()
    {
        AutoSize = true, MaximumSize = new Size(460, 0), ForeColor = Color.DarkRed
    };

    public Product? SavedProduct { get; private set; }

    public ProductEditorForm(string barcode, Func<Product, string?> save, Product? existing = null)
    {
        Text = existing is null ? "Add Product" : "Edit Product";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(560, 0);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), AutoSize = true, ColumnCount = 2
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddField("Scanned barcode", new TextBox
        {
            Text = barcode, ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Scanned barcode"
        }, 0);
        AddField("Item code", code, 1);
        AddField("Product name", description, 2);
        AddField("Size / pack", size, 3);
        AddField("Unit price ($)", unitPrice, 4);
        fields.Controls.Add(sellCase, 1, 5);
        AddField("Units per case", caseQuantity, 6);
        AddField("Case price ($)", casePrice, 7);
        fields.Controls.Add(taxable, 1, 8);
        fields.Controls.Add(feedback, 0, 9);
        fields.SetColumnSpan(feedback, 2);

        var saveButton = PosView.Button("&Save Product", primary: true);
        var cancel = PosView.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        buttons.Controls.AddRange([cancel, saveButton]);
        fields.Controls.Add(buttons, 0, 10);
        fields.SetColumnSpan(buttons, 2);
        Controls.Add(fields);
        AcceptButton = saveButton;
        CancelButton = cancel;

        code.Text = existing?.Code ?? barcode;
        description.Text = existing?.Description ?? "";
        size.Text = existing?.Size ?? "";
        unitPrice.Text = existing?.UnitPrice.ToString("F2", CultureInfo.InvariantCulture) ?? "";
        sellCase.Checked = existing?.HasCasePrice ?? false;
        caseQuantity.Text = sellCase.Checked ? existing!.UnitsPerCase.ToString(CultureInfo.InvariantCulture) : "";
        casePrice.Text = sellCase.Checked ? existing!.CasePrice.ToString("F2", CultureInfo.InvariantCulture) : "";
        taxable.Checked = existing?.IsTaxable ?? true;
        sellCase.CheckedChanged += (_, _) => SetCaseFields();
        SetCaseFields();

        Shown += (_, _) => { description.Focus(); description.SelectAll(); };
        saveButton.Click += (_, _) => SaveProduct();

        void AddField(string label, Control input, int row)
        {
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            fields.Controls.Add(input, 1, row);
        }

        void SetCaseFields()
        {
            caseQuantity.Enabled = sellCase.Checked;
            casePrice.Enabled = sellCase.Checked;
        }

        void SaveProduct()
        {
            if (string.IsNullOrWhiteSpace(code.Text))
                ShowProblem("Enter an item code.", code);
            else if (string.IsNullOrWhiteSpace(description.Text))
                ShowProblem("Enter a product name.", description);
            else if (!Sale.TryParseMoney(unitPrice.Text, out decimal unit))
                ShowProblem("Enter a unit price with up to two decimal places.", unitPrice);
            else
            {
                int units = 0;
                decimal price = 0;
                if (sellCase.Checked)
                {
                    if (!int.TryParse(caseQuantity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out units) || units < 2)
                    {
                        ShowProblem("Enter at least two units per case.", caseQuantity);
                        return;
                    }

                    if (!Sale.TryParseMoney(casePrice.Text, out price))
                    {
                        ShowProblem("Enter a case price with up to two decimal places.", casePrice);
                        return;
                    }
                }

                var product = new Product(
                    code.Text.Trim(), barcode, description.Text.Trim(), size.Text.Trim(),
                    unit, units, price, taxable.Checked);
                string? problem;
                try
                {
                    problem = save(product);
                }
                catch (SqliteException error)
                {
                    problem = $"The product could not be saved. {error.Message}";
                }

                if (problem is not null)
                {
                    ShowProblem(problem, code);
                    return;
                }

                SavedProduct = product;
                DialogResult = DialogResult.OK;
            }
        }

        void ShowProblem(string message, TextBox input)
        {
            feedback.Text = message;
            input.Focus();
            input.SelectAll();
        }
    }
}
