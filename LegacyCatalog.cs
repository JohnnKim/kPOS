using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record ImportSummary(int Products, int Vendors, int ScanCodes, string[] Review);

internal sealed partial class RegisterData
{
    // LiquorPOS fixed-field text exports. Retain complete rows for fields that kPOS does not interpret.
    private static List<string[]> ReadLegacyRows(string file, int expected)
    {
        using var input = new StreamReader(file, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        var rows = new List<string[]>();
        while (input.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            // LiquorPOS encloses text fields but also emits literal quotes inside names and sizes.
            // Only a quote at a field boundary closes a field.
            var fields = new List<string>();
            var value = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (!quoted && c == ',') { fields.Add(value.ToString()); value.Clear(); }
                else if (!quoted && c == '"' && value.Length == 0) quoted = true;
                else if (quoted && c == '"' && (i == line.Length - 1 || line[i + 1] == ',')) quoted = false;
                else if (quoted && c == '"' && i+1 < line.Length && line[i+1] == '"' &&
                         i+2 < line.Length && line[i+2] != ',') { value.Append('"'); i++; }
                else value.Append(c);
            }
            fields.Add(value.ToString());
            if (quoted || fields.Count != expected)
                throw new InvalidDataException($"Expected {expected} fields at record {rows.Count + 1} in {Path.GetFileName(file)}.");
            rows.Add(fields.ToArray());
        }
        if (rows.Count == 0) throw new InvalidDataException("The export is empty.");
        return rows;
    }

    internal ImportSummary ImportLegacyCatalog(string inventoryFile, string vendorFile, DateOnly asOf)
    {
        var items = ReadLegacyRows(inventoryFile, 72);
        var vendors = ReadLegacyRows(vendorFile, 18);
        var ambiguous = items.GroupBy(r => r[0], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r[69]).Distinct().Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var review = ambiguous.Select(barcode => $"Barcode {barcode} is assigned to different items. Search by item name until corrected.").ToList();
        using var tx = connection.BeginTransaction(deferred: false);
        using var previous = Command("SELECT 1 FROM legacy_inventory LIMIT 1", tx);
        if (previous.ExecuteScalar() is not null) throw new InvalidOperationException("This database already contains a legacy inventory import.");
        void Run(string sql, params object[] values)
        {
            using var stmt = Command(sql,tx);
            for (int i=0;i<values.Length;i++) stmt.Parameters.AddWithValue("$p"+i,values[i]);
            stmt.ExecuteNonQuery();
        }
        var vendorIds = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        int number = 0;
        foreach (var row in vendors)
        {
            Run("INSERT INTO legacy_vendors VALUES($p0,$p1)",++number,JsonSerializer.Serialize(row));
            string name=row[1].Trim();
            Run("""
                INSERT INTO purchase_distributors(name,contact,phone,email,address,account) VALUES($p0,$p1,$p2,'',$p3,'')
                ON CONFLICT(name) DO NOTHING
                """, name, row[2], row[4], string.Join(", ",row[6..11].Where(v=>!string.IsNullOrWhiteSpace(v))));
            using var id=Command("SELECT id FROM purchase_distributors WHERE name=$name",tx);
            id.Parameters.AddWithValue("$name",name);vendorIds[name]=(long)id.ExecuteScalar()!;
        }

        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var codes=Command("SELECT code,barcode FROM products",tx))
        using (var reader=codes.ExecuteReader())
            while(reader.Read()){occupied.Add(reader.GetString(0));occupied.Add(reader.GetString(1));}
        number=0;
        foreach (var row in items)
            Run("INSERT INTO legacy_inventory VALUES($p0,$p1,$p2)",++number,row[69],JsonSerializer.Serialize(row));
        int productCount=0, scanCount=0;
        var keys = ReadRegisterKeys(tx);
        foreach(var group in items.GroupBy(r=>r[69]))
        {
            int Units(string[] r) => int.Parse(r[9],CultureInfo.InvariantCulture);
            decimal Price(string[] r, int i) => decimal.Parse(r[i],CultureInfo.InvariantCulture);
            bool Available(string[] r) => !ambiguous.Contains(r[0]) && !occupied.Contains(r[0]) && !keys.IsSaleShortcut(r[0]) && Units(r)>0;
            var usable = group.Where(Available).DistinctBy(r=>r[0],StringComparer.OrdinalIgnoreCase).OrderBy(Units).ThenByDescending(r=>r[0].Length).ToArray();
            var row = usable.FirstOrDefault() ?? group.First();
            string barcode = usable.Length>0 ? row[0] : "LEGACY-"+group.Key;
            foreach(var blocked in group.Where(r=>!Available(r) && !ambiguous.Contains(r[0])).Select(r=>r[0]).Distinct())
                review.Add($"Item {group.Key}, barcode {blocked} requires review.");
            bool manualOnly = usable.Length == 0;
            int size = int.Parse(row[8],CultureInfo.InvariantCulture);
            var carton = size>1 ? usable.FirstOrDefault(r=>Units(r)==size) : null;
            var product = new Product(barcode,barcode,(row[1]+" "+row[2]).Trim(),row[4],Price(row,10),
                carton is null ? 0 : size,carton is null ? 0 : Price(carton,10),row[30]=="Y");
            if(occupied.Contains(barcode)) throw new InvalidDataException($"Item identifier {barcode} is already taken.");
            WriteProduct(product,tx);
            RecordPrice(product,null,tx);
            productCount++;
            // A row without a valid pack quantity cannot be sold until the store assigns it a pack and price.
            if(manualOnly && Units(row)<=0) Run("UPDATE products SET archived=1 WHERE barcode=$p0",barcode);
            var setup = new ItemSetup(CigTaxable:row[31]=="Y", OrderCases:row[50]=="C");
            Run("""
                INSERT INTO product_details(barcode,brand,product_type,unit_cost_cents,settings_json,edited_at)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5)
                """,barcode,row[1],row[3],Cents(decimal.Round(Price(row,11),2,MidpointRounding.AwayFromZero)),JsonSerializer.Serialize(setup),DateTimeOffset.Now.ToString("O"));
            if(vendorIds.TryGetValue(row[35].Trim(),out long vendor))
                Run("INSERT INTO distributor_items VALUES($p0,$p1)",barcode,vendor);
            Run("INSERT INTO inventory_counts VALUES($p0,$p1,$p2)",barcode,Day(asOf),long.Parse(row[15],CultureInfo.InvariantCulture));
            foreach(var scan in usable)
            {
                // Normal primary codes read the editable price from products.
                if(scan[0]!=barcode || Units(scan)!=1)
                    Run("INSERT INTO scan_aliases VALUES($p0,$p1,$p2,$p3)",scan[0],barcode,Units(scan),Cents(Price(scan,10)));
                occupied.Add(scan[0]); scanCount++;
            }
            occupied.Add(barcode);
        }
        tx.Commit();
        return new(productCount,vendorIds.Count,scanCount,review.ToArray());
    }
}
