// "Read data": writes everything SolidWorks knows EXACTLY about the active part or assembly into a JSON file -
// Hole Wizard holes (thread or plain hole, size, tap drill, countersink / counterbore, hole count), cosmetic threads,
// sheet metal (thickness, K-factor) and every bend (edge flange, sketched bend, jog = double bend, flat-pattern bends),
// weld beads, cut-list properties, custom properties (Number), configurations, material, mass, and for an assembly the
// component tree. Nothing is guessed from geometry. The file is the ground truth for building the assembly export:
// the feature data interfaces are dumped by reflection, so we see what a real part contains before deciding the format.
// Raw API units: metres and radians.
using System;
using System.Collections.Generic;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace LaserCAM.SolidWorks
{
    public class Inspector
    {
        const int MAX_DEPTH = 8;          // sub-feature nesting
        const int MAX_FEATURES = 20000;   // per document
        const int MAX_PARTS = 500;        // unique part files of an assembly

        readonly ISldWorks _sw;
        int _features;

        public Inspector(ISldWorks sw) { _sw = sw; }

        /// Number of features read by the last Run (for the confirmation message).
        public int FeatureCount { get { return _features; } }

        public string Run(IModelDoc2 model)
        {
            var root = new Dictionary<string, object>();
            root["format"] = "lasercam-solidworks-dump";
            root["version"] = 1;
            root["units"] = "raw SolidWorks API values: lengths in metres, angles in radians";
            root["addin"] = typeof(Inspector).Assembly.GetName().Version.ToString();
            Put(root, "solidworks", () => _sw.RevisionNumber());
            root["exportedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            root["document"] = Document(model);

            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var parts = new List<object>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                root["components"] = Components(model, parts, seen);
                root["parts"] = parts;
            }
            return Json.Write(root);
        }

        // ---------------- one document (part or assembly) ----------------
        Dictionary<string, object> Document(IModelDoc2 model)
        {
            var d = new Dictionary<string, object>();
            Put(d, "title", () => model.GetTitle());
            Put(d, "path", () => model.GetPathName());
            Put(d, "type", () => model.GetType() == (int)swDocumentTypes_e.swDocPART ? "part" : model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "other");
            Put(d, "activeConfiguration", () => model.ConfigurationManager.ActiveConfiguration.Name);
            Put(d, "configurations", () => model.GetConfigurationNames());
            Put(d, "properties", () => Properties(model.Extension.get_CustomPropertyManager("")));
            Put(d, "configurationProperties", () =>
            {
                var all = new Dictionary<string, object>();
                foreach (string cfg in (string[])model.GetConfigurationNames())
                {
                    var p = Properties(model.Extension.get_CustomPropertyManager(cfg));
                    if (p.Count > 0) all[cfg] = p;
                }
                return all;
            });
            Put(d, "massProperties", () =>
            {
                int status = 0;
                var m = (double[])model.Extension.GetMassProperties(1, ref status);
                return m == null ? null : new Dictionary<string, object> { { "volume", m[3] }, { "area", m[4] }, { "mass", m[5] }, { "status", status } };
            });
            var part = model as IPartDoc;
            if (part != null)
            {
                Put(d, "material", () => { string db; string name = part.GetMaterialPropertyName2("", out db); return new Dictionary<string, object> { { "name", name }, { "database", db } }; });
                // The material can differ per configuration (the owner keeps the real one in the flat-pattern configuration).
                Put(d, "materialByConfiguration", () =>
                {
                    var all = new Dictionary<string, object>();
                    foreach (string cfg in (string[])model.GetConfigurationNames())
                    {
                        string db; string name = part.GetMaterialPropertyName2(cfg, out db);
                        all[cfg] = new Dictionary<string, object> { { "name", name }, { "database", db } };
                    }
                    return all;
                });
                Put(d, "bodies", () =>
                {
                    var list = new List<object>();
                    var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    if (bodies != null)
                        foreach (IBody2 b in bodies)
                        {
                            var bd = new Dictionary<string, object>();
                            Put(bd, "name", () => b.Name);
                            Put(bd, "sheetMetal", () => b.IsSheetMetal());
                            list.Add(bd);
                        }
                    return list;
                });
            }
            _features = 0;
            Put(d, "features", () =>
            {
                var list = new List<object>();
                var f = model.FirstFeature() as IFeature;
                while (f != null && _features < MAX_FEATURES)
                {
                    list.Add(Feature(f, 0));
                    f = f.GetNextFeature() as IFeature;
                }
                return list;
            });
            d["featureCount"] = _features;
            return d;
        }

        // ---------------- features ----------------
        Dictionary<string, object> Feature(IFeature f, int depth)
        {
            _features++;
            var d = new Dictionary<string, object>();
            Put(d, "name", () => f.Name);
            Put(d, "type", () => f.GetTypeName2());
            Put(d, "suppressed", () => f.IsSuppressed());
            Put(d, "id", () => f.GetID());

            object def = null;
            try { def = f.GetDefinition(); } catch { }
            if (def != null)
            {
                // Each feature kind has its own data interface; ask for every one we care about and dump what answers.
                Data<IWizardHoleFeatureData2>(d, "holeWizard", def);
                Data<ICosmeticThreadFeatureData>(d, "cosmeticThread", def);
                Data<ISheetMetalFeatureData>(d, "sheetMetal", def);
                Data<IOneBendFeatureData>(d, "oneBend", def);
                Data<ISketchedBendFeatureData>(d, "sketchedBend", def);
                Data<IEdgeFlangeFeatureData>(d, "edgeFlange", def);
                Data<IJogFeatureData>(d, "jog", def);
                Data<IBendsFeatureData>(d, "bends", def);
                Data<IFlatPatternFeatureData>(d, "flatPattern", def);
                Data<ICosmeticWeldBeadFeatureData>(d, "weldBead", def);
            }

            // Cut-list folders (sheet metal, weldments): body count, kind and the stored cut-list properties.
            object spec = null;
            try { spec = f.GetSpecificFeature2(); } catch { }
            var folder = spec as IBodyFolder;
            if (folder != null)
            {
                Data<IBodyFolder>(d, "bodyFolder", folder);
                Put(d, "cutListProperties", () => Properties(f.CustomPropertyManager));
            }

            if (depth < MAX_DEPTH)
            {
                var subs = new List<object>();
                IFeature s = null;
                try { s = f.GetFirstSubFeature() as IFeature; } catch { }
                while (s != null && _features < MAX_FEATURES)
                {
                    subs.Add(Feature(s, depth + 1));
                    IFeature next = null;
                    try { next = s.GetNextSubFeature() as IFeature; } catch { }
                    s = next;
                }
                if (subs.Count > 0) d["subFeatures"] = subs;
            }
            return d;
        }

        /// If `obj` implements the COM interface T: every simple readable property and every parameterless Get* / Is*
        /// method of T, read through reflection (a failing member is recorded as "error: ..." and does not stop the rest).
        static void Data<T>(Dictionary<string, object> target, string key, object obj) where T : class
        {
            T typed = null;
            try { typed = obj as T; } catch { }
            if (typed == null) return;
            var d = new Dictionary<string, object>();
            foreach (PropertyInfo p in typeof(T).GetProperties())
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || !Simple(p.PropertyType)) continue;
                try { d[p.Name] = Plain(p.GetValue(typed, null)); }
                catch (Exception ex) { d[p.Name] = "error: " + Inner(ex); }
            }
            foreach (MethodInfo m in typeof(T).GetMethods())
            {
                if (m.IsSpecialName || m.GetParameters().Length > 0 || !Simple(m.ReturnType)) continue;
                if (!(m.Name.StartsWith("Get", StringComparison.Ordinal) || m.Name.StartsWith("Is", StringComparison.Ordinal))) continue;
                try { d[m.Name + "()"] = Plain(m.Invoke(typed, null)); }
                catch (Exception ex) { d[m.Name + "()"] = "error: " + Inner(ex); }
            }
            target[key] = d;
        }

        static bool Simple(Type t) { return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal); }
        static object Plain(object v)
        {
            if (v is double) { double x = (double)v; return double.IsNaN(x) || double.IsInfinity(x) ? (object)x.ToString() : x; }
            if (v is float) { float x = (float)v; return float.IsNaN(x) || float.IsInfinity(x) ? (object)x.ToString() : x; }
            return v is Enum ? Convert.ToInt32(v) : v;
        }
        static string Inner(Exception ex) { return (ex.InnerException ?? ex).Message; }

        // ---------------- custom properties ----------------
        static Dictionary<string, object> Properties(ICustomPropertyManager mgr)
        {
            var d = new Dictionary<string, object>();
            if (mgr == null) return d;
            var names = mgr.GetNames() as string[];
            if (names == null) return d;
            foreach (string name in names)
            {
                string value, resolved;
                try
                {
                    mgr.Get4(name, false, out value, out resolved);
                    d[name] = new Dictionary<string, object> { { "value", value }, { "resolved", resolved } };
                }
                catch (Exception ex) { d[name] = "error: " + ex.Message; }
            }
            return d;
        }

        // ---------------- assembly ----------------
        List<object> Components(IModelDoc2 model, List<object> parts, HashSet<string> seen)
        {
            var list = new List<object>();
            var asm = model as IAssemblyDoc;
            if (asm == null) return list;
            var comps = asm.GetComponents(false) as object[];   // false = all levels
            if (comps == null) return list;
            foreach (IComponent2 c in comps)
            {
                var d = new Dictionary<string, object>();
                Put(d, "name", () => c.Name2);
                Put(d, "path", () => c.GetPathName());
                Put(d, "configuration", () => c.ReferencedConfiguration);
                Put(d, "parent", () => { var p = c.GetParent() as IComponent2; return p == null ? null : p.Name2; });
                Put(d, "suppression", () => c.GetSuppression());   // swComponentSuppressionState_e
                Put(d, "excludeFromBOM", () => c.ExcludeFromBOM);
                Put(d, "hidden", () => c.IsHidden(false));
                Put(d, "virtual", () => c.IsVirtual);
                Put(d, "envelope", () => c.IsEnvelope());
                list.Add(d);

                // Each referenced file once: its own properties and features (only when the component is loaded).
                string path = "";
                try { path = c.GetPathName() ?? ""; } catch { }
                if (path == "" || !seen.Add(path) || parts.Count >= MAX_PARTS) continue;
                IModelDoc2 cm = null;
                try { cm = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (cm == null) { parts.Add(new Dictionary<string, object> { { "path", path }, { "notLoaded", true } }); continue; }
                int keep = _features;
                parts.Add(Document(cm));
                _features += keep;
            }
            return list;
        }

        static void Put(Dictionary<string, object> d, string key, Func<object> read)
        {
            try { d[key] = read(); }
            catch (Exception ex) { d[key] = "error: " + ex.Message; }
        }
    }
}
