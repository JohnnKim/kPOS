namespace Kpos;

internal sealed class AmountLabel : Label
{
    private readonly float points;
    private Font full;
    private Font? fitted;
    internal AmountLabel(string text,float size)
    {
        points=size;full=PosView.Text(size,FontStyle.Bold);Text=text;ForeColor=Color.White;Font=full;
        TextAlign=ContentAlignment.MiddleRight;Dock=DockStyle.Fill;Margin=Padding.Empty;MinimumSize=new Size(0,full.Height+6);
        SizeChanged+=(_,_)=>Fit();TextChanged+=(_,_)=>Fit();HandleCreated+=(_,_)=>Fit();DpiChangedAfterParent+=(_,_)=>Fit();
    }
    internal void SetViewScale(double scale)
    {
        float size=Math.Max(8.5f*96/DeviceDpi,(float)(points*scale));if(Math.Abs(full.Size-size)<.01)return;
        var previous=full;full=PosView.Text(size,FontStyle.Bold);Font=full;MinimumSize=new Size(0,full.Height+6);Fit();previous.Dispose();
    }
    private void Fit()
    {
        if(!IsHandleCreated||ClientSize.Width<1||Text.Length==0)return;
        using var graphics=CreateGraphics();int width=TextRenderer.MeasureText(graphics,Text,full,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix).Width;
        float size=width<=ClientSize.Width?full.Size:Math.Max(8,(float)Math.Floor(full.Size*(ClientSize.Width-4)/width));
        var previous=fitted;fitted=size==full.Size?null:new Font(full.FontFamily,size,FontStyle.Bold);Font=fitted??full;previous?.Dispose();
    }
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing){fitted?.Dispose();full.Dispose();}}
}
