// What a part WITHOUT a sheet metal feature is, recognised from its faces (owner 2026-11-23: "it may be a sheet part,
// it may be milled, it may be turned, it may be a laser-cut tube - make the add-in recognise such parts").
//
//   plate  - a flat plate of one thickness, cut through only (holes, slots, any outline; countersinks allowed):
//            cut from sheet, so it is sent like a sheet metal part (outline DXF + thickness)
//   tube   - a hollow thin-walled profile of one cross-section along its length (round, rectangular, ...), with or
//            without cuts in the walls: cut from tube
//   turned - a body of revolution (shaft, bushing, stepped disc)
//   milled - more than one thickness: a flat part with a step or pocket, a base with a wall of another thickness
//            on it; also a flat plate too thick to be cut from sheet (owner 2026-11-24: "a milled part is one where a
//            plane next to the main plane stands at right angles to it and has another thickness")
//   unknown - nothing of the above could be shown (several bodies, free-form surfaces, a block of one thickness, a
//            part of one thickness that is not flat): the person chooses the type when sending (PartTypes.cs); the
//            guess is offered first
//
// This file has no SolidWorks types: the faces are read in ShapeReader.cs, the rules here are tested without
// SolidWorks (test\ShapeClassifierTest.cs, run test.cmd). All input lengths are in metres (raw SolidWorks API values),
// the result is in millimetres. The result is a judgement from geometry, not a fact: LaserCAM shows it as a note
// and the user can change the type of the part there.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace LaserCAM.SolidWorks
{
    /// <summary>One face of the body, as much as the rules need.</summary>
    public sealed class ShapeFace
    {
        public const int Other = 0, Plane = 1, Cylinder = 2, Cone = 3, Torus = 4, Sphere = 5;
        public int Type;
        /// <summary>m2.</summary>
        public double Area;
        /// <summary>Plane: its normal. Cylinder, cone, torus: the axis. Unit length.</summary>
        public double[] Dir;
        /// <summary>Plane: a point on it. Cylinder, cone, torus: a point on the axis. Sphere: the centre.</summary>
        public double[] Point;
        /// <summary>Cylinder, sphere: the radius. Cone: the radius at its origin. Torus: the major radius.</summary>
        public double Radius;
        /// <summary>Plane: the number of loops (2 or more = the face has a hole in it).</summary>
        public int Loops;
        /// <summary>Any other surface (spline outline, gear tooth): unit normals sampled over the face.</summary>
        public double[][] Normals;
    }

    /// <summary>The recognised shape with its main sizes in mm (0 = does not apply).</summary>
    public sealed class PartShape
    {
        public const string Plate = "plate", Tube = "tube", Turned = "turned", Milled = "milled", Unknown = "unknown";
        /// <summary>Not a shape: a bought part. Only ever chosen by the person (<see cref="Chosen"/>).</summary>
        public const string Purchased = "purchased";
        public string Kind = Unknown;
        /// <summary>Why, in a few words (goes into the data file and the log).</summary>
        public string Why = "";
        /// <summary>Unknown shape: what it looks like most, offered first when the person is asked ("" = no idea).</summary>
        public string Guess = "";
        /// <summary>Unknown shape: the type the person chose when sending (owner 2026-11-24); "" = not chosen.</summary>
        public string Chosen = "";
        /// <summary>The geometry shows the body is not flat (walls, steps): its largest face is not its outline.</summary>
        public bool NotFlat;
        /// <summary>The recognised type, or for an unknown shape the type the person chose.</summary>
        public string Effective { get { return Kind != Unknown ? Kind : Chosen != "" ? Chosen : Unknown; } }
        public double ThicknessMm, LengthMm, DiameterMm, WallMm, WidthMm, HeightMm;
        /// <summary>Tube cross-section: "40x20x2", "Ø30x2".</summary>
        public string Section = "";
        public int Faces, Bodies;
        /// <summary>Bounding box along the model axes.</summary>
        public double[] SizeMm;

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { { "kind", Kind }, { "why", Why }, { "faces", Faces }, { "bodies", Bodies } };
            if (Kind == Unknown && Guess != "") d["guess"] = Guess;
            if (Kind == Unknown && Chosen != "") d["chosen"] = Chosen;
            if (ThicknessMm > 0) d["thicknessMm"] = ThicknessMm;
            if (LengthMm > 0) d["lengthMm"] = LengthMm;
            if (DiameterMm > 0) d["diameterMm"] = DiameterMm;
            if (WallMm > 0) d["wallMm"] = WallMm;
            if (WidthMm > 0) d["widthMm"] = WidthMm;
            if (HeightMm > 0) d["heightMm"] = HeightMm;
            if (Section != "") d["section"] = Section;
            if (SizeMm != null && SizeMm.Length == 3) d["sizeMm"] = new object[] { SizeMm[0], SizeMm[1], SizeMm[2] };
            return d;
        }

        /// <summary>"flat plate 3 mm", "round tube Ø30x2, 200 mm long", "turned part Ø30 x 80 mm", "milled part".</summary>
        public string Text()
        {
            if (Kind == Plate) return "flat plate " + ShapeClassifier.Fmt(ThicknessMm) + " mm";
            if (Kind == Tube) return "tube " + Section + ", " + ShapeClassifier.Fmt(LengthMm) + " mm long";
            if (Kind == Turned) return "turned part Ø" + ShapeClassifier.Fmt(DiameterMm) + " x " + ShapeClassifier.Fmt(LengthMm) + " mm";
            if (Kind == Milled) return "milled part";
            return "not recognised";
        }
    }

    public static class ShapeClassifier
    {
        /// <summary>A flat plate thicker than this is not taken as cut from sheet: it is a milled part.</summary>
        public const double MaxPlateMm = 30;
        /// <summary>A tube wall is at most this share of the smaller outer size; a thicker wall is a turned or milled part.</summary>
        public const double MaxWallShare = 0.2;

        const double Par = 0.9998;      // |cos| above this: parallel (about 1 degree)
        const double Perp = 0.02;       // |cos| below this: perpendicular
        const double PerpLoose = 0.05;  // the same for normals sampled from the display mesh
        const double Tol = 2e-5;        // 0.02 mm

        sealed class Axis
        {
            public double[] Dir, Point;
            /// <summary>Area of the faces that are surfaces of revolution about this axis (flat ends included).</summary>
            public double RotArea;
            /// <summary>Size of the body across the axis, in two directions at right angles.</summary>
            public double Across1, Across2;
        }

        /// <param name="faces">every face of the one solid body</param>
        /// <param name="volume">m3</param>
        /// <param name="extent">size of the body along a unit direction, m</param>
        public static PartShape Classify(IList<ShapeFace> faces, double volume, Func<double[], double> extent)
        {
            var r = new PartShape { Faces = faces == null ? 0 : faces.Count };
            if (faces == null || faces.Count == 0 || volume <= 0 || extent == null) { r.Why = "no solid geometry"; return r; }
            double total = 0, simple = 0;
            foreach (var f in faces) { total += f.Area; if (f.Type != ShapeFace.Other) simple += f.Area; }
            if (total <= 0) { r.Why = "no solid geometry"; return r; }

            var seen = new Evidence();
            if (Plate(faces, total, volume, extent, r, seen)) return r;
            Axis ax = MainAxis(faces, extent);
            if (ax != null && RoundTube(faces, total, volume, extent, ax, r)) return r;
            if (ShapedTube(faces, total, volume, extent, r)) return r;
            if (ax != null && Turned(total, extent, ax, r)) return r;
            if (simple < 0.5 * total) { r.Why = "mostly free-form surfaces"; return r; }
            Thicknesses(faces, total, volume, seen, r);
            return r;
        }

        /// <summary>What the search for a flat plate saw, kept for the rule about several thicknesses.</summary>
        sealed class Evidence
        {
            /// <summary>The largest flat face.</summary>
            public ShapeFace Main;
            /// <summary>Flat faces parallel to the main one lie between its level and the opposite side: steps, pocket floors.</summary>
            public bool Step;
            /// <summary>Both outer sides are large: a flat part (with a step or pocket in it), not a base with a wall on it.</summary>
            public bool FlatSides;
            /// <summary>Distance between the two outer sides, m (0 = not measured).</summary>
            public double T;
        }

        // ---------------- several thicknesses: milled; one thickness but not flat: perhaps bent sheet ----------------
        /// <summary>
        /// Owner 2026-11-24: "a milled part is one where a plane next to the main plane stands at right angles to it and
        /// has another thickness". A part cut or bent from sheet has ONE thickness everywhere; a step, a pocket or a
        /// wall of another thickness makes it milled. Nothing of the kind found: the shape stays unknown and the person
        /// chooses when sending ("when the type cannot be told, let the person choose"), with a suggestion.
        /// </summary>
        static void Thicknesses(IList<ShapeFace> faces, double total, double volume, Evidence seen, PartShape r)
        {
            r.Guess = PartShape.Milled;
            r.Why = "not a flat plate, a tube or a body of revolution, and no step or wall of another thickness was found";
            if (seen.Main == null) return;
            if (seen.T > 0) r.ThicknessMm = Math.Round(seen.T * 1000, 2);   // a flat body: its thickness, should the person say "sheet"
            if (!seen.Step) return;
            r.NotFlat = true;
            if (seen.FlatSides)
            {
                r.Kind = PartShape.Milled;
                r.Guess = "";
                r.ThicknessMm = 0;
                r.Why = "a flat part with a step or pocket: more than one thickness";
                return;
            }
            // A base with something standing on it. Its own thickness, and the thickness of the largest wall on it.
            double t1 = Partner(faces, seen.Main);
            ShapeFace wall = null;
            foreach (var f in faces)
                if (f.Type == ShapeFace.Plane && f.Dir != null && f.Point != null && Math.Abs(Dot(f.Dir, seen.Main.Dir)) <= Par && (wall == null || f.Area > wall.Area)) wall = f;
            double t2 = wall == null ? 0 : Partner(faces, wall);
            r.ThicknessMm = 0;
            if (t1 <= 0 || t2 <= 0) return;
            if (Math.Abs(t2 - t1) > Math.Max(Tol, 0.03 * t1))
            {
                r.Kind = PartShape.Milled;
                r.Guess = "";
                r.Why = "a wall next to the main face has another thickness: " + Fmt(t2 * 1000) + " mm against " + Fmt(t1 * 1000) + " mm";
                return;
            }
            // One thickness on both: sheet-like when the volume is that thickness times half of the surface (edges aside).
            double fill = volume / (t1 * total / 2);
            if (fill < 0.7 || fill > 1.03) return;
            r.Guess = PartShape.Plate;
            r.ThicknessMm = Math.Round(t1 * 1000, 2);
            r.Why = "one thickness everywhere (" + Fmt(t1 * 1000) + " mm) but not flat: it may be bent from sheet";
        }

        /// <summary>
        /// Thickness of the material behind a flat face: the distance to the nearest flat face parallel to it that is of
        /// a comparable size (at least 0.3 of its area - a narrow edge further on does not count). 0 = none.
        /// </summary>
        static double Partner(IList<ShapeFace> faces, ShapeFace of)
        {
            double at = Dot(of.Point, of.Dir), best = 0;
            foreach (var f in faces)
            {
                if (f == of || f.Type != ShapeFace.Plane || f.Dir == null || f.Point == null || f.Area < 0.3 * of.Area) continue;
                if (Math.Abs(Dot(f.Dir, of.Dir)) <= Par) continue;
                double d = Math.Abs(Dot(f.Point, of.Dir) - at);
                if (d > Tol && (best == 0 || d < best)) best = d;
            }
            return best;
        }

        // ---------------- flat plate ----------------
        static bool Plate(IList<ShapeFace> faces, double total, double volume, Func<double[], double> extent, PartShape r, Evidence seen)
        {
            ShapeFace big = null;
            foreach (var f in faces) if (f.Type == ShapeFace.Plane && f.Dir != null && f.Point != null && (big == null || f.Area > big.Area)) big = f;
            if (big == null) return false;
            seen.Main = big;
            double[] n = big.Dir;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var f in faces)
            {
                if (f.Type != ShapeFace.Plane || f.Dir == null || f.Point == null || Math.Abs(Dot(f.Dir, n)) <= Par) continue;
                double pos = Dot(f.Point, n);
                if (pos < lo) lo = pos;
                if (pos > hi) hi = pos;
            }
            double t = hi - lo;
            if (t < Tol) return false;
            double tol = Math.Max(Tol, t * 0.01);
            // The two sides, flat faces between them, faces that go straight through (outline, holes), countersinks,
            // and whatever is left (chamfers, fillets, slanted faces). A flat face between the sides is either the
            // floor of a round counterbore (a ring or a disc between holes along the normal: drilled, the plate is
            // still cut from sheet) or a step / pocket floor (another thickness: milled).
            var holes = new List<double>();
            foreach (var f in faces)
                if (f.Type == ShapeFace.Cylinder && f.Dir != null && Math.Abs(Dot(f.Dir, n)) > Par && !holes.Exists(x => Math.Abs(x - f.Radius) < Tol)) holes.Add(f.Radius);
            double aLo = 0, aHi = 0, aBore = 0, aStep = 0, aThrough = 0, aSink = 0;
            foreach (var f in faces)
            {
                if (f.Type == ShapeFace.Plane && f.Dir != null && f.Point != null && Math.Abs(Dot(f.Dir, n)) > Par)
                {
                    double pos = Dot(f.Point, n);
                    if (pos - lo < tol) aLo += f.Area; else if (hi - pos < tol) aHi += f.Area;
                    else if (BoreFloor(f.Area, holes)) aBore += f.Area; else aStep += f.Area;
                }
                else if (Along(f, n)) aThrough += f.Area;
                else if (f.Type == ShapeFace.Cone && f.Dir != null && Math.Abs(Dot(f.Dir, n)) > Par) aSink += f.Area;
            }
            double aMain = Math.Max(aLo, aHi);
            double aRest = total - aLo - aHi - aBore - aStep - aThrough - aSink;
            bool stands = extent(n) > t * 1.02 + Tol;                 // something stands out of the two sides
            seen.Step = aStep > 0.002 * aMain || stands;
            seen.FlatSides = !stands && Math.Min(aLo, aHi) >= 0.5 * aMain;
            if (!seen.Step) seen.T = t;
            if (aMain <= 0 || aThrough <= 0) return false;
            if (Math.Min(aLo, aHi) < 0.7 * aMain) return false;      // one side is mostly machined away
            if (seen.Step) return false;                              // a pocket or a step: another thickness
            if (aBore > 0.10 * aMain) return false;                   // more counterbore than plate
            if (aRest > 0.03 * total) return false;                   // chamfered, rounded or slanted
            if (aSink > 0.15 * total) return false;
            double fill = volume / (aMain * t);
            if (fill < 0.85 || fill > 1.03) return false;
            // Mean width of the material between the cuts (2 x area / length of all cut edges): a bar is not a plate.
            double mean = 2 * aMain * t / aThrough;
            if (mean < 1.5 * t) return false;

            double mm = Math.Round(t * 1000, 2);
            r.ThicknessMm = mm;
            if (mm > MaxPlateMm)
            {
                r.Kind = PartShape.Milled;
                r.Why = "flat plate " + Fmt(mm) + " mm thick: more than " + Fmt(MaxPlateMm) + " mm, not taken as cut from sheet";
                return true;
            }
            r.Kind = PartShape.Plate;
            r.Why = "flat plate of one thickness, cut through only";
            return true;
        }

        /// <summary>A flat face of this area is the floor of a round counterbore: a ring between two of the holes, or a disc.</summary>
        static bool BoreFloor(double area, List<double> holes)
        {
            foreach (double big in holes)
            {
                if (Math.Abs(area - Math.PI * big * big) <= 0.03 * area) return true;
                foreach (double small in holes)
                    if (small < big && Math.Abs(area - Math.PI * (big * big - small * small)) <= 0.03 * area) return true;
            }
            return false;
        }

        // ---------------- axis of revolution ----------------
        static Axis MainAxis(IList<ShapeFace> faces, Func<double[], double> extent)
        {
            Axis best = null;
            double bestArea = 0;
            var tried = new List<Axis>();
            foreach (var f in faces)
            {
                if (!Round(f)) continue;
                bool seen = false;
                foreach (var a in tried) if (Coaxial(f, a)) { seen = true; break; }
                if (seen) continue;
                var ax = new Axis { Dir = f.Dir, Point = f.Point };
                tried.Add(ax);
                double area = 0;
                foreach (var g in faces)
                    if (Coaxial(g, ax) || (g.Type == ShapeFace.Sphere && g.Point != null && OffAxis(g.Point, ax) < Tol)) area += g.Area;
                if (area > bestArea) { bestArea = area; best = ax; }
            }
            if (best == null) return null;
            double[] u = AnyPerp(best.Dir);
            double[] v = Cross(best.Dir, u);
            best.Across1 = extent(u);
            best.Across2 = extent(v);
            // Flat faces square to the axis are its ends and shoulders - but only when they fit inside the body's round
            // outline (the top of a block with one hole in it is not an end of a shaft).
            double d = Math.Max(best.Across1, best.Across2);
            double cap = Math.PI * d * d / 4 * 1.01;
            double ends = 0;
            foreach (var g in faces)
                if (g.Type == ShapeFace.Plane && g.Dir != null && Math.Abs(Dot(g.Dir, best.Dir)) > Par && g.Area <= cap) ends += g.Area;
            best.RotArea = bestArea + ends;
            return best;
        }

        static bool Round(ShapeFace f)
        {
            return (f.Type == ShapeFace.Cylinder || f.Type == ShapeFace.Cone || f.Type == ShapeFace.Torus) && f.Dir != null && f.Point != null;
        }

        static bool Coaxial(ShapeFace f, Axis a)
        {
            return Round(f) && Math.Abs(Dot(f.Dir, a.Dir)) > Par && OffAxis(f.Point, a) < Tol;
        }

        static double OffAxis(double[] p, Axis a)
        {
            double[] w = { p[0] - a.Point[0], p[1] - a.Point[1], p[2] - a.Point[2] };
            double along = Dot(w, a.Dir);
            double sq = Dot(w, w) - along * along;
            return sq <= 0 ? 0 : Math.Sqrt(sq);
        }

        // ---------------- round tube ----------------
        static bool RoundTube(IList<ShapeFace> faces, double total, double volume, Func<double[], double> extent, Axis ax, PartShape r)
        {
            // Exactly two cylinders about the axis: the outside and the bore, both along the whole length.
            var radii = new List<double[]>();   // radius, area
            foreach (var f in faces)
            {
                if (f.Type != ShapeFace.Cylinder || !Coaxial(f, ax)) continue;
                bool merged = false;
                foreach (var e in radii) if (Math.Abs(e[0] - f.Radius) < Tol) { e[1] += f.Area; merged = true; break; }
                if (!merged) radii.Add(new[] { f.Radius, f.Area });
            }
            radii.RemoveAll(e => e[1] < 0.01 * total);
            if (radii.Count != 2) return false;
            double[] outer = radii[0][0] > radii[1][0] ? radii[0] : radii[1];
            double[] inner = radii[0][0] > radii[1][0] ? radii[1] : radii[0];
            double R = outer[0], rr = inner[0], d = 2 * R, wall = R - rr;
            double len = extent(ax.Dir);
            if (len < Tol || wall < Tol || wall > MaxWallShare * d + Tol) return false;
            if (Math.Max(ax.Across1, ax.Across2) > d * 1.02 + Tol) return false;      // something stands out of the outside
            if (outer[1] < 0.6 * 2 * Math.PI * R * len || inner[1] < 0.6 * 2 * Math.PI * rr * len) return false;
            double full = Math.PI * (R * R - rr * rr) * len;
            double fill = volume / full;
            if (fill < 0.5 || fill > 1.03) return false;
            if (ax.RotArea < 0.6 * total) return false;

            r.Kind = PartShape.Tube;
            r.DiameterMm = Math.Round(d * 1000, 2);
            r.WallMm = Math.Round(wall * 1000, 2);
            r.LengthMm = Math.Round(len * 1000, 2);
            r.Section = "Ø" + Fmt(r.DiameterMm) + "x" + Fmt(r.WallMm);
            r.Why = "round tube: an outside and a bore of one size along the whole length";
            return true;
        }

        // ---------------- tube of another section (rectangular, square, ...) ----------------
        static bool ShapedTube(IList<ShapeFace> faces, double total, double volume, Func<double[], double> extent, PartShape r)
        {
            // Directions the profile may run along: where the three largest flat face directions cross, and the axes of the corner radii.
            var flat = new List<double[]>();    // nx ny nz area
            foreach (var f in faces)
            {
                if (f.Type != ShapeFace.Plane || f.Dir == null) continue;
                bool merged = false;
                foreach (var e in flat) if (Math.Abs(e[0] * f.Dir[0] + e[1] * f.Dir[1] + e[2] * f.Dir[2]) > Par) { e[3] += f.Area; merged = true; break; }
                if (!merged) flat.Add(new[] { f.Dir[0], f.Dir[1], f.Dir[2], f.Area });
            }
            flat.Sort((a, b) => b[3].CompareTo(a[3]));
            var dirs = new List<double[]>();
            int top = Math.Min(3, flat.Count);
            for (int i = 0; i < top; i++)
                for (int j = i + 1; j < top; j++)
                    AddDir(dirs, Unit(Cross(flat[i], flat[j])));
            foreach (var f in faces) if (f.Type == ShapeFace.Cylinder && f.Dir != null) AddDir(dirs, f.Dir);
            double[] dir = null;
            double along = 0;
            foreach (var c in dirs)
            {
                double a = 0;
                foreach (var f in faces) if (Along(f, c)) a += f.Area;
                if (a > along) { along = a; dir = c; }
            }
            if (dir == null || along < 0.8 * total) return false;       // the walls must be nearly all of the surface

            // Hollow: an end face with a hole in it.
            bool hollow = false;
            foreach (var f in faces)
                if (f.Type == ShapeFace.Plane && f.Dir != null && Math.Abs(Dot(f.Dir, dir)) > 0.5 && f.Loops >= 2) { hollow = true; break; }
            if (!hollow) return false;
            // The outer sizes are measured along the largest wall and across it.
            double[] u = null;
            foreach (var e in flat) if (Math.Abs(e[0] * dir[0] + e[1] * dir[1] + e[2] * dir[2]) < Perp) { u = new[] { e[0], e[1], e[2] }; break; }
            if (u == null) return false;
            double[] v = Unit(Cross(dir, u));
            if (v == null) return false;
            double w = extent(u), h = extent(v), len = extent(dir);
            if (w < Tol || h < Tol || len < Tol) return false;
            // Wall = 2 x volume / wall area: exact for one wall thickness, also with cuts in the walls.
            double wall = 2 * volume / along;
            if (wall > MaxWallShare * Math.Min(w, h) + Tol) return false;
            if (volume / (w * h * len) > 0.6) return false;

            r.Kind = PartShape.Tube;
            r.WidthMm = Math.Round(Math.Max(w, h) * 1000, 2);
            r.HeightMm = Math.Round(Math.Min(w, h) * 1000, 2);
            r.WallMm = Math.Round(wall * 1000, 2);
            r.LengthMm = Math.Round(len * 1000, 2);
            r.Section = Fmt(r.WidthMm) + "x" + Fmt(r.HeightMm) + "x" + Fmt(r.WallMm);
            r.Why = "hollow profile of one cross-section along its length";
            return true;
        }

        static void AddDir(List<double[]> dirs, double[] d)
        {
            if (d == null) return;
            foreach (var e in dirs) if (Math.Abs(Dot(e, d)) > Par) return;
            dirs.Add(d);
        }

        // ---------------- body of revolution ----------------
        static bool Turned(double total, Func<double[], double> extent, Axis ax, PartShape r)
        {
            if (ax.RotArea < 0.85 * total) return false;
            double d = Math.Max(ax.Across1, ax.Across2);
            if (d < Tol || Math.Abs(ax.Across1 - ax.Across2) > 0.02 * d + Tol) return false;
            r.Kind = PartShape.Turned;
            r.DiameterMm = Math.Round(d * 1000, 2);
            r.LengthMm = Math.Round(extent(ax.Dir) * 1000, 2);
            r.Why = "body of revolution";
            return true;
        }

        // ---------------- helpers ----------------
        /// <summary>The face runs along the direction everywhere (a wall of an extrusion along it).</summary>
        static bool Along(ShapeFace f, double[] d)
        {
            if (f.Type == ShapeFace.Plane) return f.Dir != null && Math.Abs(Dot(f.Dir, d)) < Perp;
            if (f.Type == ShapeFace.Cylinder) return f.Dir != null && Math.Abs(Dot(f.Dir, d)) > Par;
            if (f.Type != ShapeFace.Other || f.Normals == null || f.Normals.Length == 0) return false;
            foreach (var n in f.Normals) if (n == null || Math.Abs(Dot(n, d)) > PerpLoose) return false;
            return true;
        }

        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        public static double[] Unit(double[] v)
        {
            if (v == null || v.Length < 3) return null;
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return len < 1e-12 ? null : new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        static double[] AnyPerp(double[] d)
        {
            return Unit(Cross(Math.Abs(d[2]) < 0.9 ? new double[] { 0, 0, 1 } : new double[] { 1, 0, 0 }, d));
        }

        public static string Fmt(double mm) { return Math.Round(mm, 1).ToString("0.#", CultureInfo.InvariantCulture); }
    }
}
