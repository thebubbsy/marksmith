using System;
using System.Collections.Generic;
using MdToPdf.Core.Services;

class Program {
    static void Main() {
        var shapes = new List<ImageToWordShapesForge.VectorShape>();
        shapes.Add(new ImageToWordShapesForge.VectorShape("line", 10, 10, 20, 20, "#ff0000", 1.5, "", 0, 0));
        shapes.Add(new ImageToWordShapesForge.VectorShape("line", 20, 20, 30, 30, "#ff0000", 1.5, "", 0, 0));
        
        var type = typeof(ImageToWordShapesForge);
        var method = type.GetMethod("BuildSvgBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method.Invoke(null, new object[] { shapes });
        Console.WriteLine(result);
    }
}
