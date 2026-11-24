// Reads the faces of a part from SolidWorks and has ShapeClassifier say what the part is (plate, tube, turned,
// milled). Used for parts WITHOUT a sheet metal feature. Nothing in the model is changed or selected.
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace LaserCAM.SolidWorks
{
    public static class ShapeReader
    {
        const int MaxNormals = 300;   // sampled normals kept per free-form face

        // One answer per file within one command (a part is asked about several times during a send).
        static readonly Dictionary<string, PartShape> _seen = new Dictionary<string, PartShape>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget the answers: called when a command starts, so an edited part is measured again.</summary>
        public static void Reset() { _seen.Clear(); }

        /// <summary>Never throws: a part that cannot be measured is "unknown" with the reason.</summary>
        public static PartShape Of(IModelDoc2 model)
        {
            string key = "";
            try { key = model != null ? (model.GetPathName() ?? "") : ""; } catch { key = ""; }
            PartShape s;
            if (key != "" && _seen.TryGetValue(key, out s)) return s;
            try { s = Read(model); }
            catch (Exception ex) { s = new PartShape { Why = "the geometry could not be read: " + ex.Message }; }
            // Not recognised: the type the person chose for this part file in an earlier send, if any (PartTypes.cs).
            if (s.Kind == PartShape.Unknown) s.Chosen = PartTypes.Get(key);
            if (key != "") _seen[key] = s;
            return s;
        }

        static PartShape Read(IModelDoc2 model)
        {
            var part = model as IPartDoc;
            if (part == null) return new PartShape { Why = "not a part" };
            var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            int count = bodies == null ? 0 : bodies.Length;
            if (count != 1) return new PartShape { Bodies = count, Why = count == 0 ? "no solid body" : count + " solid bodies" };
            var body = bodies[0] as IBody2;
            var raw = body != null ? body.GetFaces() as object[] : null;
            if (raw == null) return new PartShape { Bodies = 1, Why = "no faces" };

            var faces = new List<ShapeFace>();
            foreach (object o in raw)
            {
                var face = o as IFace2;
                if (face != null) faces.Add(FaceOf(face));
            }
            double volume = 0;
            try
            {
                var mp = body.GetMassProperties(1.0) as double[];   // centre x y z, volume, area, mass, ...
                if (mp != null && mp.Length > 3) volume = mp[3];
            }
            catch { volume = 0; }
            if (volume <= 0)
            {
                int status = 0;
                var m = model.Extension.GetMassProperties(1, ref status) as double[];
                if (m != null && m.Length > 3) volume = m[3];
            }

            PartShape s = ShapeClassifier.Classify(faces, volume, d => Extent(body, d));
            s.Bodies = 1;
            try
            {
                var box = body.GetBodyBox() as double[];   // x y z of the lower corner, x y z of the upper one
                if (box != null && box.Length >= 6)
                    s.SizeMm = new[] { Math.Round((box[3] - box[0]) * 1000, 2), Math.Round((box[4] - box[1]) * 1000, 2), Math.Round((box[5] - box[2]) * 1000, 2) };
            }
            catch { /* the sizes stay unknown */ }
            return s;
        }

        /// <summary>Size of the body along a unit direction (metres).</summary>
        static double Extent(IBody2 body, double[] d)
        {
            double x1, y1, z1, x2, y2, z2;
            if (!body.GetExtremePoint(d[0], d[1], d[2], out x1, out y1, out z1)) return 0;
            if (!body.GetExtremePoint(-d[0], -d[1], -d[2], out x2, out y2, out z2)) return 0;
            return Math.Abs((x1 - x2) * d[0] + (y1 - y2) * d[1] + (z1 - z2) * d[2]);
        }

        static ShapeFace FaceOf(IFace2 face)
        {
            var f = new ShapeFace();
            try { f.Area = face.GetArea(); } catch { f.Area = 0; }
            try
            {
                var surf = face.GetSurface() as ISurface;
                if (surf == null) return f;
                if (surf.IsPlane())
                {
                    var p = surf.PlaneParams as double[];          // normal x y z, a point x y z
                    if (Set(f, ShapeFace.Plane, p, 0, 3, 6)) { try { f.Loops = face.GetLoopCount(); } catch { f.Loops = 1; } }
                }
                else if (surf.IsCylinder())
                {
                    var p = surf.CylinderParams as double[];       // a point on the axis x y z, the axis x y z, radius
                    if (Set(f, ShapeFace.Cylinder, p, 3, 0, 7)) f.Radius = p[6];
                }
                else if (surf.IsCone())
                {
                    var p = surf.ConeParams as double[];           // origin x y z, the axis x y z, radius, half angle
                    if (Set(f, ShapeFace.Cone, p, 3, 0, 7)) f.Radius = p[6];
                }
                else if (surf.IsTorus())
                {
                    var p = surf.TorusParams as double[];          // centre x y z, the axis x y z, major radius, minor radius
                    if (Set(f, ShapeFace.Torus, p, 3, 0, 7)) f.Radius = p[6];
                }
                else if (surf.IsSphere())
                {
                    var p = surf.SphereParams as double[];         // centre x y z, radius
                    if (p != null && p.Length >= 4) { f.Type = ShapeFace.Sphere; f.Point = new[] { p[0], p[1], p[2] }; f.Radius = p[3]; }
                }
                else f.Normals = Normals(face);
            }
            catch { f.Type = ShapeFace.Other; }
            return f;
        }

        /// <summary>Fills the direction (3 numbers from `dirAt`) and the point (3 numbers from `pointAt`); false when the data is not there.</summary>
        static bool Set(ShapeFace f, int type, double[] p, int dirAt, int pointAt, int need)
        {
            if (p == null || p.Length < need) return false;
            double[] dir = ShapeClassifier.Unit(new[] { p[dirAt], p[dirAt + 1], p[dirAt + 2] });
            if (dir == null) return false;
            f.Type = type;
            f.Dir = dir;
            f.Point = new[] { p[pointAt], p[pointAt + 1], p[pointAt + 2] };
            return true;
        }

        /// <summary>
        /// Normals of a free-form face, taken from its display mesh (3 numbers per triangle corner). They only tell
        /// whether the face stands straight on a plate or runs along a profile; null when the mesh is not there.
        /// </summary>
        static double[][] Normals(IFace2 face)
        {
            try
            {
                var arr = face.GetTessNorms() as Array;
                if (arr == null || arr.Length < 3) return null;
                int corners = arr.Length / 3;
                int step = Math.Max(1, corners / MaxNormals);
                var list = new List<double[]>();
                for (int i = 0; i < corners; i += step)
                {
                    double[] n = ShapeClassifier.Unit(new[]
                    {
                        Convert.ToDouble(arr.GetValue(3 * i)), Convert.ToDouble(arr.GetValue(3 * i + 1)), Convert.ToDouble(arr.GetValue(3 * i + 2)),
                    });
                    if (n != null) list.Add(n);
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch { return null; }
        }
    }
}
