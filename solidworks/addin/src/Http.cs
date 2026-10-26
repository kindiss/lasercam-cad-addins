// HTTP + JSON helpers (JavaScriptSerializer: objects -> Dictionary<string,object>, arrays -> object[]).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace LaserCAM.SolidWorks
{
    public static class Json
    {
        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }; }
        public static string Write(object o) { return Ser().Serialize(o); }
        public static object Read(string s) { return string.IsNullOrEmpty(s) ? null : Ser().DeserializeObject(s); }
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static object[] Arr(object o) { return o as object[] ?? new object[0]; }
        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            if (v is bool) return (bool)v ? "True" : "";   // Odoo: empty char field = false
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static double Num(Dictionary<string, object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            double r; return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : 0;
        }
        /// many2one value [id, "name"] -> id
        public static int M2oId(object v) { var a = v as object[]; return a != null && a.Length > 0 ? Convert.ToInt32(a[0]) : (v is int ? (int)v : 0); }
        public static string M2oName(object v) { var a = v as object[]; return a != null && a.Length > 1 ? Convert.ToString(a[1]) : ""; }
        public static string NumStr(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    public static class Http
    {
        static Http()
        {
            // .NET 4.5 defaults to TLS 1.0; both LaserCAM and Odoo need TLS 1.2.
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | ServicePointManager.SecurityProtocol;
        }

        /// Returns the response body; status is set even on 4xx/5xx (no exception).
        public static string Send(string method, string url, string body, Dictionary<string, string> headers, out int status, int timeoutMs = 600000)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            // A redirect (e.g. ucase.eu -> www.ucase.eu) would be followed as GET and Odoo answers 404
            // "Did you mean POST ...". Do not follow; report the real address instead.
            req.AllowAutoRedirect = false;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (method != "GET")
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            HttpWebResponse res;
            try { res = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                res = ex.Response as HttpWebResponse;
                // no response at all = no connection / wrong host / timeout -> plain-language text (not a raw WebException)
                if (res == null) throw new Exception("Cannot reach " + new Uri(url).GetLeftPart(UriPartial.Authority) + " (" + ex.Message + ").\n\nCheck the address in Settings (ODOO_URL / LC_API), the internet connection and VPN.");
            }
            using (res)
            {
                status = (int)res.StatusCode;
                if (status == 301 || status == 302 || status == 307 || status == 308)
                {
                    string loc = res.Headers["Location"] ?? "";
                    Uri target;
                    string host = Uri.TryCreate(new Uri(url), loc, out target) ? target.GetLeftPart(UriPartial.Authority) : loc;
                    throw new Exception("The server redirects " + new Uri(url).GetLeftPart(UriPartial.Authority) + " to " + host + ". Use that address in Settings (ODOO_URL / LC_API).");
                }
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }

    /// Odoo JSON-2 API: POST {url}/json/2/{model}/{method} with kwargs body, bearer API key.
    public class OdooClient
    {
        readonly string _url, _apiKey, _db;
        public OdooClient(string url, string apiKey, string db) { _url = url.TrimEnd('/'); _apiKey = apiKey; _db = db; }

        /// Plain-language text for a failed Odoo request (shown to the user instead of the raw HTTP status).
        string OdooMessage(int status, string resp, string what)
        {
            string tail = resp.Length > 300 ? resp.Substring(0, 300) : resp;
            if (status == 401 || status == 403)
            {
                if (string.IsNullOrEmpty((_apiKey ?? "").Trim()))
                    return "Odoo rejected the request because the API key is empty (HTTP " + status + ").\n\nSettings -> ODOO_API_KEY: in Odoo open My profile > Account security > New API key, copy the key and paste it there. Keys are stored encrypted and cannot be viewed again - if lost, create a new one.";
                return "Odoo rejected the API key (HTTP " + status + ").\n\nThe key in Settings is wrong, revoked or belongs to another Odoo. Create a new key in Odoo (My profile > Account security > New API key) and paste it into ODOO_API_KEY. On-premise Odoo with several databases also needs ODOO_DB.";
            }
            if (status == 404)
                return "Odoo did not answer at " + _url + " (HTTP 404, " + what + ").\n\nCheck ODOO_URL in Settings. This direct connection needs Odoo 19 or newer; for older Odoo use the LaserCAM Connector module (Odoo App Store).";
            if (status >= 500)
                return "Odoo server error (HTTP " + status + ") on " + what + ":\n" + tail;
            return "Odoo " + what + " -> HTTP " + status + ": " + tail;
        }

        public object Call(string model, string method, Dictionary<string, object> kwargs)
        {
            var h = new Dictionary<string, string> { { "Authorization", "bearer " + _apiKey } };
            if (!string.IsNullOrEmpty(_db)) h["X-Odoo-Database"] = _db;
            int status;
            string resp = Http.Send("POST", _url + "/json/2/" + model + "/" + method, Json.Write(kwargs), h, out status, 120000);
            if (status < 200 || status >= 300)
                throw new Exception(OdooMessage(status, resp, model + "." + method));
            return Json.Read(resp);
        }

        public List<Dictionary<string, object>> SearchRead(string model, object[] domain, string[] fields, int limit)
        {
            var r = Call(model, "search_read", new Dictionary<string, object> { { "domain", domain }, { "fields", fields }, { "limit", limit } });
            var list = new List<Dictionary<string, object>>();
            foreach (var row in Json.Arr(r)) { var d = Json.Obj(row); if (d != null) list.Add(d); }
            return list;
        }

        public int Create(string model, Dictionary<string, object> vals)
        {
            var r = Call(model, "create", new Dictionary<string, object> { { "vals_list", new object[] { vals } } });
            var a = r as object[];
            return a != null && a.Length > 0 ? Convert.ToInt32(a[0]) : Convert.ToInt32(r);
        }

        public void Write(string model, int id, Dictionary<string, object> vals)
        {
            Call(model, "write", new Dictionary<string, object> { { "ids", new object[] { id } }, { "vals", vals } });
        }
    }

    /// LaserCAM server: session token, browser job, headless quote, job result.
    public class LaserCamClient
    {
        readonly string _api;
        readonly string _pid, _key;
        string _token;
        public LaserCamClient(string api, string pid, string key) { _api = api.TrimEnd('/'); _pid = pid; _key = key; }

        public void EnsureToken()
        {
            if (!string.IsNullOrEmpty(_token)) return;
            if (string.IsNullOrEmpty(_key) || _pid == "0" || string.IsNullOrEmpty(_pid))
                throw new Exception("Fill in LC_PID and LC_KEY in the LaserCAM settings.");
            int status;
            string resp = Http.Send("POST", _api + "/session", "{\"pid\":" + _pid + ",\"key\":" + Json.Write(_key) + "}", null, out status, 30000);
            if (status != 200) throw new Exception("LaserCAM session failed (" + status + "): check LC_PID / LC_KEY and the plan.");
            _token = Json.Str(Json.Obj(Json.Read(resp)), "token");
            if (string.IsNullOrEmpty(_token)) throw new Exception("LaserCAM session: no token.");
        }

        Dictionary<string, string> Auth() { return new Dictionary<string, string> { { "Authorization", "Bearer " + _token } }; }

        /// POST with one automatic re-login on 401.
        public Dictionary<string, object> Post(string path, object body)
        {
            EnsureToken();
            int status;
            string json = Json.Write(body);
            string resp = Http.Send("POST", _api + path, json, Auth(), out status);
            if (status == 401) { _token = null; EnsureToken(); resp = Http.Send("POST", _api + path, json, Auth(), out status); }
            if (status == 403)
            {
                var d = Json.Obj(Json.Read(resp));
                string e = d != null ? Json.Str(d, "error") : "";
                throw new Exception(e.StartsWith("plan") ? "Your LaserCAM plan does not include this: " + e.Substring(5).Trim() + " (laser.ucase.eu)" : "LaserCAM " + path + " error 403: " + e);
            }
            if (status != 200) throw new Exception("LaserCAM " + path + " error " + status + ": " + (resp.Length > 300 ? resp.Substring(0, 300) : resp));
            return Json.Obj(Json.Read(resp)) ?? new Dictionary<string, object>();
        }

        /// 200 -> result object, 204 -> null (not ready yet); other statuses throw.
        public Dictionary<string, object> GetResult(string jobId)
        {
            EnsureToken();
            int status;
            string resp = Http.Send("GET", _api + "/job/" + Uri.EscapeDataString(jobId) + "/result", null, Auth(), out status, 30000);
            if (status == 204) return null;
            if (status != 200) throw new Exception("LaserCAM result error " + status + ": " + (resp.Length > 300 ? resp.Substring(0, 300) : resp));
            return Json.Obj(Json.Read(resp));
        }
    }
}
