using System.Drawing.Drawing2D;

namespace Kpos;

internal enum RegisterIcon { Checkout, Lookup, Catalog, Closeout, Reports, Orders, Backup, Settings }

internal static class RegisterActionIcons
{
    public static void Set(Button button, RegisterIcon icon)
    {
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.HandleCreated += (_, _) => Refresh();
        button.DpiChangedAfterParent += (_, _) => Refresh();
        button.Disposed += (_, _) => button.Image?.Dispose();
        Refresh();

        void Refresh()
        {
            int size = button.LogicalToDeviceUnits(18);
            if (button.Image?.Width == size)
                return;

            Image? previous = button.Image;
            var bitmap = new Bitmap(size, size);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size / 18f, size / 18f);
            using var pen = new Pen(button.ForeColor, 1.5f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };

            switch (icon)
            {
                case RegisterIcon.Checkout:
                    graphics.DrawRectangle(pen, 2, 4, 13, 10);
                    graphics.DrawLine(pen, 2, 7, 15, 7);
                    graphics.DrawLine(pen, 5, 11, 8, 11);
                    break;
                case RegisterIcon.Lookup:
                    graphics.DrawEllipse(pen, 2, 2, 9, 9);
                    graphics.DrawLine(pen, 10, 10, 15, 15);
                    break;
                case RegisterIcon.Catalog:
                    graphics.DrawRectangle(pen, 2, 3, 13, 12);
                    graphics.DrawLine(pen, 7, 3, 7, 15);
                    graphics.DrawLine(pen, 2, 9, 15, 9);
                    break;
                case RegisterIcon.Closeout:
                    graphics.DrawEllipse(pen, 2, 2, 13, 13);
                    graphics.DrawLines(pen, [new Point(8, 5), new Point(8, 9), new Point(11, 10)]);
                    break;
                case RegisterIcon.Reports:
                    graphics.DrawLine(pen, 3, 14, 15, 14);
                    graphics.DrawLine(pen, 5, 11, 5, 7);
                    graphics.DrawLine(pen, 9, 11, 9, 4);
                    graphics.DrawLine(pen, 13, 11, 13, 6);
                    break;
                case RegisterIcon.Orders:
                    graphics.DrawRectangle(pen, 3, 3, 12, 12);
                    graphics.DrawLine(pen, 3, 7, 15, 7);
                    graphics.DrawLine(pen, 9, 3, 9, 7);
                    break;
                case RegisterIcon.Backup:
                    graphics.DrawLines(pen, [new Point(2, 11), new Point(2, 15), new Point(15, 15), new Point(15, 11)]);
                    graphics.DrawLine(pen, 8, 2, 8, 11);
                    graphics.DrawLines(pen, [new Point(5, 8), new Point(8, 11), new Point(11, 8)]);
                    break;
                case RegisterIcon.Settings:
                    graphics.DrawLine(pen, 2, 5, 15, 5);
                    graphics.DrawLine(pen, 2, 12, 15, 12);
                    graphics.DrawLine(pen, 6, 3, 6, 7);
                    graphics.DrawLine(pen, 11, 10, 11, 14);
                    break;
            }

            button.Image = bitmap;
            previous?.Dispose();
        }
    }
}
