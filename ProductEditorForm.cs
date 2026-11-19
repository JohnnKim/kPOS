using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class ProductEditorForm : PosForm
{
    private readonly TextBox barcodeInput = new() { MaxLength = 80, Dock = DockStyle.Fill, AccessibleName = "Barcode" };
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

    public ProductEditorForm(string barcode, Func<Product, string?> save, Product? existing = null, RegisterData? data = null)
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

        barcodeInput.Text = barcode;
        barcodeInput.ReadOnly = barcode.Length > 0;
        AddField(barcodeInput.ReadOnly ? "Scanned barcode" : "Barcode", barcodeInput, 0);
        AddField("Item code (optional)", code, 1);
        AddField("Product name", description, 2);
        AddField("Size / pack", size, 3);
        AddField("Unit price ($)", unitPrice, 4);
        fields.Controls.Add(sellCase, 1, 5);
        AddField("Units per case", caseQuantity, 6);
        AddField("Case price ($)", casePrice, 7);
        fields.Controls.Add(taxable, 1, 8);
        var brand = new TextBox { Dock = DockStyle.Fill, MaxLength = 80, AccessibleName = "Brand" };
        var type = new TextBox { Dock = DockStyle.Fill, MaxLength = 60, AccessibleName = "Product type" };
        var vendorCode = new TextBox { Dock = DockStyle.Fill, MaxLength = 100, AccessibleName = "Supplier item code" };
        var cost = new TextBox { Dock = DockStyle.Fill, MaxLength = 13, AccessibleName = "Purchase unit cost" };
        var supplier = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Supplier" };
        CatalogDetails meta = existing is null ? new() : data?.ReadCatalogDetails().GetValueOrDefault(existing.Barcode) ?? new();
        brand.Text = meta.Brand; type.Text = meta.Type; vendorCode.Text = meta.VendorCode;
        cost.Text = meta.UnitCost?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        supplier.Items.Add(new SupplierChoice(null, "Not assigned"));
        if (data is not null)
            foreach (Distributor distributor in data.ReadDistributors()) supplier.Items.Add(new SupplierChoice(distributor.Id, distributor.Details.Name));
        supplier.SelectedIndex = 0;
        for (int i = 0; i < supplier.Items.Count; i++)
            if (((SupplierChoice)supplier.Items[i]!).Id == meta.SupplierId) supplier.SelectedIndex = i;
        int footer = 9;
        if (data is not null)
        {
            AddField("Brand", brand, footer++);
            AddField("Type / department", type, footer++);
            AddField("Supplier", supplier, footer++);
            AddField("Supplier item code", vendorCode, footer++);
            AddField("Unit cost ($)", cost, footer++);
        }
        fields.Controls.Add(feedback, 0, footer++);
        fields.SetColumnSpan(feedback, 2);

        var saveButton = PosView.Button("&Save Product", primary: true);
        var cancel = PosView.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        buttons.Controls.AddRange([cancel, saveButton]);
        fields.Controls.Add(buttons, 0, footer);
        fields.SetColumnSpan(buttons, 2);
        Controls.Add(fields);
        AcceptButton = saveButton;
        CancelButton = cancel;

        code.Text = existing is not null && existing.Code != existing.Barcode ? existing.Code : "";
        description.Text = existing?.Description ?? "";
        size.Text = existing?.Size ?? "";
        unitPrice.Text = existing?.UnitPrice.ToString("F2", CultureInfo.InvariantCulture) ?? "";
        sellCase.Checked = existing?.HasCasePrice ?? false;
        caseQuantity.Text = sellCase.Checked ? existing!.UnitsPerCase.ToString(CultureInfo.InvariantCulture) : "";
        casePrice.Text = sellCase.Checked ? existing!.CasePrice.ToString("F2", CultureInfo.InvariantCulture) : "";
        taxable.Checked = existing?.IsTaxable ?? true;
        sellCase.CheckedChanged += (_, _) => SetCaseFields();
        SetCaseFields();

        Shown += (_, _) =>
        {
            TextBox first = barcodeInput.ReadOnly ? description : barcodeInput;
            first.Focus();
            first.SelectAll();
        };
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
            if (string.IsNullOrWhiteSpace(barcodeInput.Text))
                ShowProblem("Enter a barcode.", barcodeInput);
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
                    string.IsNullOrWhiteSpace(code.Text) ? barcodeInput.Text.Trim() : code.Text.Trim(),
                    barcodeInput.Text.Trim(), description.Text.Trim(), size.Text.Trim(),
                    unit, units, price, taxable.Checked);
                string? problem;
                try
                {
                    decimal? unitCost = null;
                    if (!string.IsNullOrWhiteSpace(cost.Text))
                    {
                        if (!Sale.TryParseMoney(cost.Text, out decimal parsed)) { ShowProblem("Enter a valid unit cost.", cost); return; }
                        unitCost = parsed;
                    }
                    problem = data is null ? save(product) : data.SaveCatalogProduct(product, existing,
                        new(brand.Text, type.Text, vendorCode.Text, unitCost, ((SupplierChoice)supplier.SelectedItem!).Id));
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

    private sealed record SupplierChoice(long? Id, string Name) { public override string ToString() => Name; }
}
