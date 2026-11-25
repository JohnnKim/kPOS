namespace Kpos;

// Resizes one logical layout instead of independently stretching its axes.
internal sealed class ProportionalLayout : IDisposable
{
    private readonly Control root;
    private readonly Dictionary<Control,(string Family,float Size,FontStyle Style)> fonts=[];
    private readonly Dictionary<DataGridView,GridSize> grids=[];
    private readonly Dictionary<(string,float,FontStyle),Font> owned=[];
    private readonly Dictionary<DataGridViewCellStyle,(string Family,float Size,FontStyle Style)> cellFonts=[];
    private sealed record Metrics(Padding Margin,Padding Padding,Size Minimum,float[] Rows,float[] Columns);
    private readonly Dictionary<Control,Metrics> metrics=[];
    private readonly Dictionary<Control,Size> fixedLabels=[];
    private double scale=1;
    private bool applying;
    private bool applied;
    private sealed record GridSize(int Row,int Header, Dictionary<DataGridViewColumn,(int Width,int Minimum)> Columns);
    internal ProportionalLayout(Control root){this.root=root;Capture(root);}
    private void Capture(Control control)
    {
        if(!fonts.ContainsKey(control))
        {
            Font font=control.Font;
            bool explicitFont=System.ComponentModel.TypeDescriptor.GetProperties(control)["Font"]?.ShouldSerializeValue(control)??false;
            fonts[control]=(font.FontFamily.Name,explicitFont?font.Size:(float)(font.Size/scale),font.Style);
            control.ControlAdded+=Added;
            if(control is RegisterClock)fixedLabels[control]=control.Size;
            metrics[control]=new(control.Margin,control.Padding,control.MinimumSize,
                control is TableLayoutPanel rows?rows.RowStyles.Cast<RowStyle>().Select(row=>row.SizeType==SizeType.Absolute?row.Height:-1).ToArray():[],
                control is TableLayoutPanel columns?columns.ColumnStyles.Cast<ColumnStyle>().Select(column=>column.SizeType==SizeType.Absolute?column.Width:-1).ToArray():[]);
        }
        // DataGridView columns do not follow the initial compact Control.Scale.
        // Keep their reference widths in the same coordinate system as the canvas.
        if(control is DataGridView grid&&!grids.ContainsKey(grid))
            grids[grid]=new(grid.RowTemplate.Height,grid.ColumnHeadersHeight,grid.Columns.Cast<DataGridViewColumn>().ToDictionary(column=>column,column=>((int)Math.Round(column.Width*PosView.CompactScale),(int)Math.Round(column.MinimumWidth*PosView.CompactScale))));
        if(control is DataGridView table)
        {
            foreach(var style in new[]{table.DefaultCellStyle,table.ColumnHeadersDefaultCellStyle}.Concat(table.Columns.Cast<DataGridViewColumn>().SelectMany(column=>new[]{column.DefaultCellStyle,column.HeaderCell.Style})))
                if(style.Font is Font font&&!cellFonts.ContainsKey(style))cellFonts[style]=(font.FontFamily.Name,font.Size,font.Style);
        }
        if(control is not DataGridView)foreach(Control child in control.Controls)Capture(child);
    }
    private void Added(object? sender,ControlEventArgs e)
    {
        if(applying||sender is DataGridView||e.Control is null||e.Control is HScrollBar or VScrollBar||fonts.ContainsKey(e.Control))return;
        applying=true;
        try{e.Control.Scale(new SizeF(PosView.CompactScale,PosView.CompactScale));Capture(e.Control);e.Control.Scale(new SizeF((float)scale,(float)scale));ApplyFonts(e.Control);}
        finally{applying=false;}
    }
    internal void Apply(double factor)
    {
        if(applying||applied&&Math.Abs(scale-factor)<.002)return;
        applying=true;
        PosView.SuspendLayoutTree(root);
        try
        {
            Capture(root);
            float relative=(float)(factor/scale);root.Scale(new SizeF(relative,relative));scale=factor;applied=true;
            foreach(var (control,original) in metrics.Where(pair=>!pair.Key.IsDisposed))
            {
                Padding Resize(Padding padding)=>new((int)Math.Round(padding.Left*scale),(int)Math.Round(padding.Top*scale),(int)Math.Round(padding.Right*scale),(int)Math.Round(padding.Bottom*scale));
                control.Margin=Resize(original.Margin);control.Padding=Resize(original.Padding);
                if(control is not AmountLabel)control.MinimumSize=new((int)Math.Round(original.Minimum.Width*scale),(int)Math.Round(original.Minimum.Height*scale));
                if(control is TableLayoutPanel table)
                {
                    for(int i=0;i<Math.Min(original.Rows.Length,table.RowStyles.Count);i++)if(original.Rows[i]>=0)table.RowStyles[i].Height=(float)(original.Rows[i]*scale);
                    for(int i=0;i<Math.Min(original.Columns.Length,table.ColumnStyles.Count);i++)if(original.Columns[i]>=0)table.ColumnStyles[i].Width=(float)(original.Columns[i]*scale);
                }
            }
            foreach(var (control,size) in fixedLabels.Where(pair=>!pair.Key.IsDisposed))control.Size=new((int)Math.Round(size.Width*scale),(int)Math.Round(size.Height*scale));
            ApplyFonts(root);
            foreach(var (style,font) in cellFonts)style.Font=FontFor(font.Family,Math.Max(9.5f*96/root.DeviceDpi,(float)(font.Size*scale)),font.Style);
            foreach(var (grid,size) in grids.Where(pair=>!pair.Key.IsDisposed))
            {
                grid.RowTemplate.Height=Math.Max(18,(int)Math.Round(size.Row*scale));if(grid.ColumnHeadersHeightSizeMode!=DataGridViewColumnHeadersHeightSizeMode.AutoSize)grid.ColumnHeadersHeight=Math.Max(24,(int)Math.Round(size.Header*scale));
                foreach(var (column,dimensions) in size.Columns.Where(pair=>pair.Key.DataGridView==grid))
                {
                    column.MinimumWidth=Math.Max(2,(int)Math.Round(dimensions.Minimum*scale));
                    if(column.AutoSizeMode==DataGridViewAutoSizeColumnMode.None || column.AutoSizeMode==DataGridViewAutoSizeColumnMode.NotSet && grid.AutoSizeColumnsMode==DataGridViewAutoSizeColumnsMode.None)column.Width=Math.Max(column.MinimumWidth,(int)Math.Round(dimensions.Width*scale));
                }
            }
        }
        finally{PosView.ResumeLayoutTree(root);applying=false;}
    }
    private void ApplyFonts(Control control)
    {
        if(control.IsDisposed)return;
        if(control is AmountLabel amount)amount.SetViewScale(scale);
        else if(fonts.TryGetValue(control,out var original))
        {
            var key=(original.Family,Math.Max((root.FindForm() is RegisterForm ? 1f : control.AccessibleName=="Register display message"?16f:9.5f)*96/root.DeviceDpi,(float)Math.Round(original.Size*scale,2)),original.Style);
            if(!owned.TryGetValue(key,out Font? font)){font=new Font(key.Family,key.Item2,key.Style);owned[key]=font;}
            control.Font=font;
            if(control is DataGridView grid)
            {grid.DefaultCellStyle.Font=font;grid.ColumnHeadersDefaultCellStyle.Font=FontFor(original.Family,key.Item2,FontStyle.Bold);}
        }
        foreach(Control child in control.Controls)ApplyFonts(child);
    }
    private Font FontFor(string family,float size,FontStyle style)
    {var key=(family,size,style);if(!owned.TryGetValue(key,out var font)){font=new Font(family,size,style);owned[key]=font;}return font;}
    internal void DpiChanged(double ratio)
    {
        foreach(var control in fixedLabels.Keys.ToArray()){var size=fixedLabels[control];fixedLabels[control]=new((int)Math.Round(size.Width*ratio),(int)Math.Round(size.Height*ratio));}
        foreach(var control in metrics.Keys.ToArray())
        {
            var value=metrics[control];Padding Resize(Padding p)=>new((int)Math.Round(p.Left*ratio),(int)Math.Round(p.Top*ratio),(int)Math.Round(p.Right*ratio),(int)Math.Round(p.Bottom*ratio));
            metrics[control]=new(Resize(value.Margin),Resize(value.Padding),new((int)Math.Round(value.Minimum.Width*ratio),(int)Math.Round(value.Minimum.Height*ratio)),value.Rows.Select(size=>size<0?size:(float)(size*ratio)).ToArray(),value.Columns.Select(size=>size<0?size:(float)(size*ratio)).ToArray());
        }
foreach(var grid in grids.Keys.ToArray()){var old=grids[grid];grids[grid]=new((int)(old.Row*ratio),(int)(old.Header*ratio),old.Columns.ToDictionary(pair=>pair.Key,pair=>((int)(pair.Value.Width*ratio),(int)(pair.Value.Minimum*ratio))));}}
    public void Dispose(){foreach(var font in owned.Values)font.Dispose();owned.Clear();}
}
