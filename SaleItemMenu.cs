namespace Kpos;

internal static class SaleItemMenu
{
    internal static void Attach(DataGridView grid,Func<bool> editable,params (string Text,Action Action)[] actions)
    {
        var menu=new ContextMenuStrip();bool emptyClick=false;
        foreach(var (text,action) in actions)menu.Items.Add(text,null,(_,_)=>action());
        grid.ContextMenuStrip=menu;
        grid.MouseDown+=(_,e)=>
        {
            if(e.Button!=MouseButtons.Right)return;
            var hit=grid.HitTest(e.X,e.Y);emptyClick=hit.RowIndex<0;
            if(emptyClick)return;
            grid.ClearSelection();grid.CurrentCell=grid.Rows[hit.RowIndex].Cells[Math.Max(0,hit.ColumnIndex)];grid.Rows[hit.RowIndex].Selected=true;
        };
        grid.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Apps||e.Shift&&e.KeyCode==Keys.F10)emptyClick=false;};
        menu.Opening+=(_,e)=>{e.Cancel=emptyClick||grid.CurrentRow is null||!editable();};
        grid.Disposed+=(_,_)=>menu.Dispose();
    }
}
