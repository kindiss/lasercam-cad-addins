// Tests of the shape rules (src\ShapeClassifier.cs) on bodies described by hand: no SolidWorks needed.
// Run: test.cmd in the add-in folder. Sizes are written in mm and converted to metres, as SolidWorks gives them.
using System;
using System.Collections.Generic;
using LaserCAM.SolidWorks;

static class ShapeClassifierTest
{
    const double MM = 0.001, MM2 = 1e-6, MM3 = 1e-9;
    static int _failed, _run;

    static ShapeFace Plane(double nx, double ny, double nz, double px, double py, double pz, double areaMm2, int loops = 1)
    {
        return new ShapeFace { Type = ShapeFace.Plane, Dir = new[] { nx, ny, nz }, Point = new[] { px * MM, py * MM, pz * MM }, Area = areaMm2 * MM2, Loops = loops };
    }
    static ShapeFace Cyl(double ax, double ay, double az, double px, double py, double pz, double radiusMm, double areaMm2)
    {
        return new ShapeFace { Type = ShapeFace.Cylinder, Dir = new[] { ax, ay, az }, Point = new[] { px * MM, py * MM, pz * MM }, Radius = radiusMm * MM, Area = areaMm2 * MM2 };
    }
    static ShapeFace Cone(double ax, double ay, double az, double px, double py, double pz, double radiusMm, double areaMm2)
    {
        return new ShapeFace { Type = ShapeFace.Cone, Dir = new[] { ax, ay, az }, Point = new[] { px * MM, py * MM, pz * MM }, Radius = radiusMm * MM, Area = areaMm2 * MM2 };
    }
    static ShapeFace Free(double areaMm2, params double[][] normals)
    {
        return new ShapeFace { Type = ShapeFace.Other, Area = areaMm2 * MM2, Normals = normals };
    }

    /// Size of a box along a direction.
    static Func<double[], double> Box(double x, double y, double z)
    {
        return d => (Math.Abs(d[0]) * x + Math.Abs(d[1]) * y + Math.Abs(d[2]) * z) * MM;
    }
    /// Size of a cylinder (axis Z, diameter d, length l) along a direction.
    static Func<double[], double> Rod(double d, double l)
    {
        return v => (Math.Abs(v[2]) * l + Math.Sqrt(v[0] * v[0] + v[1] * v[1]) * d) * MM;
    }

    /// A box x by y by z with its six faces (lower corner at the origin).
    static List<ShapeFace> BoxFaces(double x, double y, double z)
    {
        return new List<ShapeFace>
        {
            Plane(0, 0, 1, 0, 0, z, x * y), Plane(0, 0, -1, 0, 0, 0, x * y),
            Plane(0, 1, 0, 0, y, 0, x * z), Plane(0, -1, 0, 0, 0, 0, x * z),
            Plane(1, 0, 0, x, 0, 0, y * z), Plane(-1, 0, 0, 0, 0, 0, y * z),
        };
    }

    static void Check(string name, PartShape s, string kind, Func<PartShape, bool> more = null)
    {
        _run++;
        bool ok = s.Kind == kind && (more == null || more(s));
        if (!ok) _failed++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "  ->  " + s.Kind + " (" + s.Text() + "; " + s.Why + ")" + (ok ? "" : "   EXPECTED " + kind));
    }

    static bool Near(double a, double b) { return Math.Abs(a - b) < 0.02; }

    static int Main()
    {
        const double PI = Math.PI;

        // 1. Plate 100 x 60 x 3 with two holes of 6 mm.
        {
            double hole = PI * 9, top = 6000 - 2 * hole;
            var f = BoxFaces(100, 60, 3);
            f[0].Area = top * MM2; f[0].Loops = 3; f[1].Area = top * MM2; f[1].Loops = 3;
            f.Add(Cyl(0, 0, 1, 20, 30, 0, 3, PI * 6 * 3)); f.Add(Cyl(0, 0, 1, 80, 30, 0, 3, PI * 6 * 3));
            Check("plate 100x60x3 with holes", ShapeClassifier.Classify(f, top * 3 * MM3, Box(100, 60, 3)), "plate", s => Near(s.ThicknessMm, 3));
        }
        // 2. The same plate with the two holes countersunk on the top side.
        {
            double hole = PI * 9, sink = PI * (36 - 9), bottom = 6000 - 2 * hole, top = bottom - 2 * sink;
            var f = BoxFaces(100, 60, 3);
            f[0].Area = top * MM2; f[0].Loops = 3; f[1].Area = bottom * MM2; f[1].Loops = 3;
            f.Add(Cyl(0, 0, 1, 20, 30, 0, 3, PI * 6 * 1.5)); f.Add(Cyl(0, 0, 1, 80, 30, 0, 3, PI * 6 * 1.5));
            f.Add(Cone(0, 0, 1, 20, 30, 3, 6, 120)); f.Add(Cone(0, 0, 1, 80, 30, 3, 6, 120));
            Check("plate with countersinks", ShapeClassifier.Classify(f, (bottom * 3 - 2 * 60) * MM3, Box(100, 60, 3)), "plate", s => Near(s.ThicknessMm, 3));
        }
        // 3. Gear-like plate: the outline is a spline surface standing straight on the plate.
        {
            var f = new List<ShapeFace>
            {
                Plane(0, 0, 1, 0, 0, 4, 2000, 2), Plane(0, 0, -1, 0, 0, 0, 2000, 2),
                Free(700, new[] { 1.0, 0, 0 }, new[] { 0.6, 0.8, 0 }, new[] { -0.7071, 0.7071, 0.01 }),
                Cyl(0, 0, 1, 0, 0, 0, 5, PI * 10 * 4),
            };
            Check("plate with a spline outline", ShapeClassifier.Classify(f, 2000 * 4 * MM3, Box(60, 60, 4)), "plate", s => Near(s.ThicknessMm, 4));
        }
        // 4. Round disc 50 x 3.
        {
            double a = PI * 625;
            var f = new List<ShapeFace> { Plane(0, 0, 1, 0, 0, 3, a), Plane(0, 0, -1, 0, 0, 0, a), Cyl(0, 0, 1, 0, 0, 0, 25, PI * 50 * 3) };
            Check("disc 50x3", ShapeClassifier.Classify(f, a * 3 * MM3, Rod(50, 3)), "plate", s => Near(s.ThicknessMm, 3));
        }
        // 5. Plate 100 x 60 x 8 with a pocket 50 x 30, 4 deep: milled.
        {
            var f = BoxFaces(100, 60, 8);
            f[0].Area = 4500 * MM2; f[0].Loops = 2;
            f.Add(Plane(0, 0, 1, 25, 15, 4, 1500));
            f.Add(Plane(0, 1, 0, 25, 15, 4, 200)); f.Add(Plane(0, -1, 0, 25, 45, 4, 200));
            f.Add(Plane(1, 0, 0, 25, 15, 4, 120)); f.Add(Plane(-1, 0, 0, 75, 15, 4, 120));
            Check("plate with a pocket", ShapeClassifier.Classify(f, (6000 * 8 - 1500 * 4) * MM3, Box(100, 60, 8)), "milled");
        }
        // 6. Block 100 x 60 x 40 with four holes: one thickness, cut through only, but far too narrow for its thickness
        // to be a plate - nothing says what it is, the person is asked ("milled" is suggested).
        {
            double hole = PI * 25, top = 6000 - 4 * hole;
            var f = BoxFaces(100, 60, 40);
            f[0].Area = top * MM2; f[0].Loops = 5; f[1].Area = top * MM2; f[1].Loops = 5;
            for (int i = 0; i < 4; i++) f.Add(Cyl(0, 0, 1, 20 + 20 * i, 30, 0, 5, PI * 10 * 40));
            Check("block with holes", ShapeClassifier.Classify(f, top * 40 * MM3, Box(100, 60, 40)), "unknown", s => s.Guess == "milled" && !s.NotFlat && Near(s.ThicknessMm, 40));
        }
        // 7. Flat plate 300 x 200 x 40: flat, but thicker than sheet.
        Check("plate 40 mm thick", ShapeClassifier.Classify(BoxFaces(300, 200, 40), 300.0 * 200 * 40 * MM3, Box(300, 200, 40)), "milled", s => Near(s.ThicknessMm, 40));
        // 8. Square bar 20 x 20 x 300: not a plate, and nothing says it is milled - the person is asked.
        Check("square bar", ShapeClassifier.Classify(BoxFaces(300, 20, 20), 300.0 * 20 * 20 * MM3, Box(300, 20, 20)), "unknown", s => s.Guess == "milled" && !s.NotFlat);

        // 9. Round tube 30 x 2, 200 long, one hole of 8 mm cut through both walls.
        {
            double end = PI * (225 - 169), cut = 50;
            var f = new List<ShapeFace>
            {
                Cyl(0, 0, 1, 0, 0, 0, 15, PI * 30 * 200 - 2 * cut), Cyl(0, 0, 1, 0, 0, 0, 13, PI * 26 * 200 - 2 * cut),
                Plane(0, 0, 1, 0, 0, 200, end, 2), Plane(0, 0, -1, 0, 0, 0, end, 2),
                Cyl(1, 0, 0, 0, 0, 100, 4, PI * 8 * 2), Cyl(1, 0, 0, 0, 0, 100, 4, PI * 8 * 2),
            };
            Check("round tube 30x2 with a cut", ShapeClassifier.Classify(f, (end * 200 - 2 * cut * 2) * MM3, Rod(30, 200)), "tube",
                s => Near(s.DiameterMm, 30) && Near(s.WallMm, 2) && Near(s.LengthMm, 200) && s.Section == "Ø30x2");
        }
        // 10. Rectangular tube 40 x 20 x 2, 500 long.
        {
            var f = new List<ShapeFace>
            {
                Plane(0, 1, 0, 0, 20, 0, 20000), Plane(0, -1, 0, 0, 0, 0, 20000), Plane(1, 0, 0, 40, 0, 0, 10000), Plane(-1, 0, 0, 0, 0, 0, 10000),
                Plane(0, -1, 0, 0, 18, 0, 18000), Plane(0, 1, 0, 0, 2, 0, 18000), Plane(-1, 0, 0, 38, 0, 0, 8000), Plane(1, 0, 0, 2, 0, 0, 8000),
                Plane(0, 0, 1, 0, 0, 500, 224, 2), Plane(0, 0, -1, 0, 0, 0, 224, 2),
            };
            Check("rectangular tube 40x20x2", ShapeClassifier.Classify(f, 224.0 * 500 * MM3, Box(40, 20, 500)), "tube",
                s => Near(s.WidthMm, 40) && Near(s.HeightMm, 20) && Near(s.WallMm, 2) && Near(s.LengthMm, 500) && s.Section == "40x20x2");
        }
        // 11. Ring cut from a tube: 50 x 3, 10 long (too narrow to be cut from a 10 mm sheet).
        {
            double end = PI * (625 - 484);
            var f = new List<ShapeFace>
            {
                Cyl(0, 0, 1, 0, 0, 0, 25, PI * 50 * 10), Cyl(0, 0, 1, 0, 0, 0, 22, PI * 44 * 10),
                Plane(0, 0, 1, 0, 0, 10, end, 2), Plane(0, 0, -1, 0, 0, 0, end, 2),
            };
            Check("ring 50x3", ShapeClassifier.Classify(f, end * 10 * MM3, Rod(50, 10)), "tube", s => Near(s.WallMm, 3));
        }

        // 12. Stepped shaft: 20 x 50 and 30 x 30.
        {
            var f = new List<ShapeFace>
            {
                Cyl(0, 0, 1, 0, 0, 0, 10, PI * 20 * 50), Cyl(0, 0, 1, 0, 0, 0, 15, PI * 30 * 30),
                Plane(0, 0, -1, 0, 0, 0, PI * 100), Plane(0, 0, -1, 0, 0, 50, PI * 125, 2), Plane(0, 0, 1, 0, 0, 80, PI * 225),
            };
            Check("stepped shaft", ShapeClassifier.Classify(f, (PI * 100 * 50 + PI * 225 * 30) * MM3, Rod(30, 80)), "turned", s => Near(s.DiameterMm, 30) && Near(s.LengthMm, 80));
        }
        // 13. Stepped shaft 20 x 70 and 30 x 10: its volume is close to a tube of 30 x 5, it must not become one.
        {
            var f = new List<ShapeFace>
            {
                Cyl(0, 0, 1, 0, 0, 0, 10, PI * 20 * 70), Cyl(0, 0, 1, 0, 0, 0, 15, PI * 30 * 10),
                Plane(0, 0, -1, 0, 0, 0, PI * 100), Plane(0, 0, -1, 0, 0, 70, PI * 125, 2), Plane(0, 0, 1, 0, 0, 80, PI * 225),
            };
            Check("shaft that weighs like a tube", ShapeClassifier.Classify(f, (PI * 100 * 70 + PI * 225 * 10) * MM3, Rod(30, 80)), "turned");
        }
        // 14. Thick-walled bushing 30 / 14, 40 long: the wall is too thick for a tube.
        {
            double end = PI * (225 - 49);
            var f = new List<ShapeFace>
            {
                Cyl(0, 0, 1, 0, 0, 0, 15, PI * 30 * 40), Cyl(0, 0, 1, 0, 0, 0, 7, PI * 14 * 40),
                Plane(0, 0, 1, 0, 0, 40, end, 2), Plane(0, 0, -1, 0, 0, 0, end, 2),
            };
            Check("thick bushing", ShapeClassifier.Classify(f, end * 40 * MM3, Rod(30, 40)), "turned");
        }
        // 15. Block 100 x 100 x 35 with one bore of 10: the bore does not make it a turned part.
        {
            double top = 10000 - PI * 25;
            var f = BoxFaces(100, 100, 35);
            f[0].Area = top * MM2; f[0].Loops = 2; f[1].Area = top * MM2; f[1].Loops = 2;
            f.Add(Cyl(0, 0, 1, 50, 50, 0, 5, PI * 10 * 35));
            Check("block with a bore", ShapeClassifier.Classify(f, top * 35 * MM3, Box(100, 100, 35)), "unknown", s => s.Guess == "milled");
        }
        // 16. Block 60 x 60 x 100 with a bore of 20 along it: hollow, but the wall is far too thick for a tube.
        {
            double end = 3600 - PI * 100;
            var f = BoxFaces(60, 60, 100);
            f[0].Area = end * MM2; f[0].Loops = 2; f[1].Area = end * MM2; f[1].Loops = 2;
            f.Add(Cyl(0, 0, 1, 30, 30, 0, 10, PI * 20 * 100));
            Check("bored block", ShapeClassifier.Classify(f, end * 100 * MM3, Box(60, 60, 100)), "unknown", s => s.Guess == "milled");
        }
        // 17. Free-form body (casting, printed part).
        {
            var f = new List<ShapeFace> { Free(9000, new[] { 0.0, 0, 1 }, new[] { 0.5, 0.5, 0.7071 }), Plane(0, 0, -1, 0, 0, 0, 2000) };
            Check("free-form body", ShapeClassifier.Classify(f, 50000 * MM3, Box(60, 50, 30)), "unknown");
        }
        // 18. Nothing to measure.
        Check("no faces", ShapeClassifier.Classify(new List<ShapeFace>(), 0, Box(1, 1, 1)), "unknown");

        // Owner 2026-11-24: "a milled part is one where a plane next to the main plane stands at right angles to it and
        // has another thickness"; "when the type cannot be told, let the person choose".
        // 19. Plate 100 x 60 x 8 with a small pocket 10 x 10, 4 deep: two thicknesses, so milled however small the pocket.
        {
            var f = BoxFaces(100, 60, 8);
            f[0].Area = 5900 * MM2; f[0].Loops = 2;
            f.Add(Plane(0, 0, 1, 45, 25, 4, 100));
            f.Add(Plane(0, 1, 0, 45, 25, 4, 40)); f.Add(Plane(0, -1, 0, 45, 35, 4, 40));
            f.Add(Plane(1, 0, 0, 45, 25, 4, 40)); f.Add(Plane(-1, 0, 0, 55, 25, 4, 40));
            Check("plate with a small pocket", ShapeClassifier.Classify(f, (6000 * 8 - 100 * 4) * MM3, Box(100, 60, 8)), "milled");
        }
        // 20. Plate 100 x 60 x 10 with two counterbored holes (6 through, 11 x 5 deep): still cut from sheet.
        {
            double top = 6000 - 2 * PI * 30.25, bottom = 6000 - 2 * PI * 9, floor = PI * (30.25 - 9);
            var f = BoxFaces(100, 60, 10);
            f[0].Area = top * MM2; f[0].Loops = 3; f[1].Area = bottom * MM2; f[1].Loops = 3;
            for (int i = 0; i < 2; i++)
            {
                double x = 20 + 60 * i;
                f.Add(Plane(0, 0, 1, x, 30, 5, floor, 2));
                f.Add(Cyl(0, 0, 1, x, 30, 0, 5.5, PI * 11 * 5)); f.Add(Cyl(0, 0, 1, x, 30, 0, 3, PI * 6 * 5));
            }
            double vol = 60000 - 2 * (PI * 30.25 * 5 + PI * 9 * 5);
            Check("plate with counterbores", ShapeClassifier.Classify(f, vol * MM3, Box(100, 60, 10)), "plate", s => Near(s.ThicknessMm, 10));
        }
        // 21. Milled angle: base 100 x 50 x 10 with a wall 5 thick and 30 high along one edge - the wall has another thickness.
        {
            var f = new List<ShapeFace>
            {
                Plane(0, 0, -1, 0, 0, 0, 5000), Plane(0, 0, 1, 0, 5, 10, 4500), Plane(0, 0, 1, 0, 0, 40, 500),
                Plane(0, -1, 0, 0, 0, 0, 4000), Plane(0, 1, 0, 0, 5, 10, 3000), Plane(0, 1, 0, 0, 50, 0, 1000),
                Plane(-1, 0, 0, 0, 0, 0, 650), Plane(1, 0, 0, 100, 0, 0, 650),
            };
            Check("milled angle, wall of another thickness", ShapeClassifier.Classify(f, 65000 * MM3, Box(100, 50, 40)), "milled", s => s.NotFlat);
        }
        // 22. The same outline bent from 2 mm sheet (no sheet metal feature): one thickness everywhere, so it is not
        // called milled - the person chooses, and "sheet" is suggested.
        {
            var f = new List<ShapeFace>
            {
                Plane(0, 0, -1, 0, 0, 0, 5000), Plane(0, 0, 1, 0, 2, 2, 4800), Plane(0, 0, 1, 0, 0, 30, 200),
                Plane(0, -1, 0, 0, 0, 0, 3000), Plane(0, 1, 0, 0, 2, 2, 2800), Plane(0, 1, 0, 0, 50, 0, 200),
                Plane(-1, 0, 0, 0, 0, 0, 156), Plane(1, 0, 0, 100, 0, 0, 156),
            };
            Check("angle of one thickness (bent sheet)", ShapeClassifier.Classify(f, 15600 * MM3, Box(100, 50, 30)), "unknown",
                s => s.Guess == "plate" && Near(s.ThicknessMm, 2) && s.NotFlat);
        }
        // 23. Channel 60 x 40 bent from 3 mm sheet with rounded bends (inside radius 3), 200 long.
        {
            double end = 48 * 3 + 2 * 34 * 3 + 2 * PI / 4 * (36 - 9);
            var f = new List<ShapeFace>
            {
                Plane(0, 0, -1, 0, 6, 0, 9600), Plane(0, 0, 1, 0, 6, 3, 9600), Plane(0, 0, 1, 0, 0, 40, 600), Plane(0, 0, 1, 0, 57, 40, 600),
                Plane(0, -1, 0, 0, 0, 6, 6800), Plane(0, 1, 0, 0, 3, 6, 6800), Plane(0, -1, 0, 0, 57, 6, 6800), Plane(0, 1, 0, 0, 60, 6, 6800),
                Cyl(1, 0, 0, 0, 6, 6, 6, PI / 2 * 6 * 200), Cyl(1, 0, 0, 0, 6, 6, 3, PI / 2 * 3 * 200),
                Cyl(1, 0, 0, 0, 54, 6, 6, PI / 2 * 6 * 200), Cyl(1, 0, 0, 0, 54, 6, 3, PI / 2 * 3 * 200),
                Plane(-1, 0, 0, 0, 0, 0, end), Plane(1, 0, 0, 200, 0, 0, end),
            };
            Check("channel of one thickness (bent sheet)", ShapeClassifier.Classify(f, end * 200 * MM3, Box(200, 60, 40)), "unknown",
                s => s.Guess == "plate" && Near(s.ThicknessMm, 3) && s.NotFlat);
        }
        // 24. What the person chose stands in for an unknown shape - and never replaces a recognised one.
        {
            var s = new PartShape { Chosen = "turned" };
            _run++;
            bool ok = s.Effective == "turned" && (string)s.ToJson()["chosen"] == "turned";
            var known = new PartShape { Kind = "plate", Chosen = "milled" };
            ok = ok && known.Effective == "plate" && !known.ToJson().ContainsKey("chosen");
            if (!ok) _failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + "the chosen type stands in for an unknown shape only");
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "ALL " + _run + " PASSED" : _failed + " OF " + _run + " FAILED");
        return _failed;
    }
}
