// One-shot tool: renders the PilotEars AppLogo DrawingImage (copied from
// MainWindow.xaml) into a multi-resolution .ico file used as the .exe icon
// and Velopack setup icon. Run with `dotnet run` — outputs PilotEars.ico
// in the working directory.

using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    const string LogoXaml = """
<DrawingImage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <DrawingImage.Drawing>
    <DrawingGroup>
      <GeometryDrawing>
        <GeometryDrawing.Brush>
          <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#60a5fa" Offset="0"/>
            <GradientStop Color="#2563eb" Offset="0.5"/>
            <GradientStop Color="#1e3a8a" Offset="1"/>
          </LinearGradientBrush>
        </GeometryDrawing.Brush>
        <GeometryDrawing.Geometry>
          <RectangleGeometry Rect="0,0,64,64" RadiusX="14" RadiusY="14"/>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing>
        <GeometryDrawing.Brush>
          <RadialGradientBrush GradientOrigin="0.3,0.15" Center="0.3,0.15" RadiusX="0.7" RadiusY="0.55">
            <GradientStop Color="#45ffffff" Offset="0"/>
            <GradientStop Color="#00ffffff" Offset="1"/>
          </RadialGradientBrush>
        </GeometryDrawing.Brush>
        <GeometryDrawing.Geometry>
          <RectangleGeometry Rect="0,0,64,64" RadiusX="14" RadiusY="14"/>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing>
        <GeometryDrawing.Pen><Pen Brush="#30ffffff" Thickness="1"/></GeometryDrawing.Pen>
        <GeometryDrawing.Geometry>
          <RectangleGeometry Rect="1.5,1.5,61,61" RadiusX="13" RadiusY="13"/>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing>
        <GeometryDrawing.Pen><Pen Brush="White" Thickness="3.5" StartLineCap="Round" EndLineCap="Round"/></GeometryDrawing.Pen>
        <GeometryDrawing.Geometry>
          <PathGeometry>
            <PathFigure StartPoint="15,30">
              <BezierSegment Point1="15,12" Point2="49,12" Point3="49,30"/>
            </PathFigure>
          </PathGeometry>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="White">
        <GeometryDrawing.Geometry><RectangleGeometry Rect="8,28,13,18" RadiusX="4.5" RadiusY="4.5"/></GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="White">
        <GeometryDrawing.Geometry><RectangleGeometry Rect="43,28,13,18" RadiusX="4.5" RadiusY="4.5"/></GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="#1e3a8a">
        <GeometryDrawing.Geometry>
          <GeometryGroup>
            <RectangleGeometry Rect="11,32,7,1.2" RadiusX="0.6" RadiusY="0.6"/>
            <RectangleGeometry Rect="11,36,7,1.2" RadiusX="0.6" RadiusY="0.6"/>
            <RectangleGeometry Rect="11,40,7,1.2" RadiusX="0.6" RadiusY="0.6"/>
          </GeometryGroup>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="#1e3a8a">
        <GeometryDrawing.Geometry>
          <GeometryGroup>
            <RectangleGeometry Rect="46,32,7,1.2" RadiusX="0.6" RadiusY="0.6"/>
            <RectangleGeometry Rect="46,36,7,1.2" RadiusX="0.6" RadiusY="0.6"/>
            <RectangleGeometry Rect="46,40,7,1.2" RadiusX="0.6" RadiusY="0.6"/>
          </GeometryGroup>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing>
        <GeometryDrawing.Pen><Pen Brush="White" Thickness="2.6" StartLineCap="Round" EndLineCap="Round"/></GeometryDrawing.Pen>
        <GeometryDrawing.Geometry>
          <PathGeometry>
            <PathFigure StartPoint="14,46">
              <BezierSegment Point1="14,53" Point2="17,57" Point3="24,57"/>
            </PathFigure>
          </PathGeometry>
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="White">
        <GeometryDrawing.Geometry><RectangleGeometry Rect="24,54.5,7,5" RadiusX="2.5" RadiusY="2.5"/></GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="#7dd3fc">
        <GeometryDrawing.Geometry><EllipseGeometry Center="36,57" RadiusX="1.6" RadiusY="1.6"/></GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="#bae6fd">
        <GeometryDrawing.Geometry><EllipseGeometry Center="42,57" RadiusX="1.3" RadiusY="1.3"/></GeometryDrawing.Geometry>
      </GeometryDrawing>
      <GeometryDrawing Brush="#e0f2fe">
        <GeometryDrawing.Geometry><EllipseGeometry Center="48,57" RadiusX="1" RadiusY="1"/></GeometryDrawing.Geometry>
      </GeometryDrawing>
    </DrawingGroup>
  </DrawingImage.Drawing>
</DrawingImage>
""";

    [STAThread]
    static int Main()
    {
    var img = (DrawingImage)XamlReader.Parse(LogoXaml);
    var drawing = img.Drawing;

    // Sizes we ship in the .ico. 256 is required for modern Explorer thumbnail.
    int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    var pngs = new List<byte[]>();
    foreach (var size in sizes)
    {
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            // Source DrawingImage is 64x64 — scale to target size.
            var scale = size / 64.0;
            ctx.PushTransform(new ScaleTransform(scale, scale));
            ctx.DrawDrawing(drawing);
            ctx.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        pngs.Add(ms.ToArray());
    }

    // Assemble ICO: ICONDIR (6 bytes) + N * ICONDIRENTRY (16 bytes) + payloads.
    var outPath = "PilotEars.ico";
    using var fs = File.Create(outPath);
    using var w = new BinaryWriter(fs);

    w.Write((ushort)0);              // reserved
    w.Write((ushort)1);              // type: 1 = icon
    w.Write((ushort)sizes.Length);   // image count

    int dataOffset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s));  // width
        w.Write((byte)(s >= 256 ? 0 : s));  // height
        w.Write((byte)0);                   // color palette
        w.Write((byte)0);                   // reserved
        w.Write((ushort)1);                 // color planes
        w.Write((ushort)32);                // bits per pixel
        w.Write((uint)pngs[i].Length);      // size of image data
        w.Write((uint)dataOffset);          // offset to image data
        dataOffset += pngs[i].Length;
    }
    foreach (var png in pngs) w.Write(png);

    Console.WriteLine($"Wrote {outPath} ({fs.Length} bytes, {sizes.Length} frames)");
        return 0;
    }
}
