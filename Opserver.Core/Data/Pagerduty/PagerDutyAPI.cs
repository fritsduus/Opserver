﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using StackExchange.Profiling;
using Jil;

namespace StackExchange.Opserver.Data.PagerDuty
{
    public partial class PagerDutyAPI : SinglePollNode<PagerDutyAPI>
    {
        internal static Options JilOptions = new Options(
            dateFormat: DateTimeFormat.ISO8601,
            unspecifiedDateTimeKindBehavior: UnspecifiedDateTimeKindBehavior.IsUTC,
            excludeNulls: true
            );

        public PagerDutySettings Settings { get; internal set; }
        public override string NodeType => nameof(PagerDutyAPI);
        public override int MinSecondsBetweenPolls => 3600;

        protected override IEnumerable<MonitorStatus> GetMonitorStatus()
        {
            if (OnCallUsers.ContainsData)
            {
                foreach (var a in GetSchedule())
                    yield return a.MonitorStatus;
            }
            if (Incidents.ContainsData)
            {
                foreach (var i in Incidents.Data)
                    yield return i.MonitorStatus;
            }
            yield return MonitorStatus.Good;
        }
        protected override string GetMonitorStatusReason() => "";
        public string APIKey => Settings.APIKey;

        public override IEnumerable<Cache> DataPollers
        {
            get
            {
                yield return OnCallUsers;
                yield return AllUsers;
                yield return Incidents;
                yield return AllSchedules;
            }
        }

        private Cache<T> GetPagerDutyCache<T>(
            TimeSpan cacheDuration,
            Func<Task<T>> get,
            bool logExceptions = true,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0
            ) where T : class
        {
            return new Cache<T>(this, "PagerDuty - API: " + memberName,
                cacheDuration,
                get,
                logExceptions: logExceptions,
                memberName: memberName,
                sourceFilePath: sourceFilePath,
                sourceLineNumber: sourceLineNumber
            );
        }

        public PagerDutyAPI()
        {
            Settings = Current.Settings.PagerDuty;
        }

        /// <summary>
        /// Gets content from the PagerDuty API
        /// </summary>
        /// <typeparam name="T">Type to return</typeparam>
        /// <param name="path">The path to return, including any query string</param>
        /// <param name="getFromJson"></param>
        /// <param name="httpMethod"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<T> GetFromPagerDutyAsync<T>(string path, Func<string, T> getFromJson, string httpMethod = "GET", object data = null)
        {
            var url = Settings.APIBaseUrl;
            var fullUri = url + path;
            
            using (MiniProfiler.Current.CustomTiming("http", fullUri, httpMethod))
            {
                var req = (HttpWebRequest)WebRequest.Create(fullUri);
                req.Method = httpMethod;
                req.Headers.Add("Authorization: Token token=" + APIKey);

                if (httpMethod == "POST" || httpMethod == "PUT")
                {
                    
                    if (data != null)
                    {
                        var stringData = JSON.Serialize(data, JilOptions);
                        var byteData = new ASCIIEncoding().GetBytes(stringData);
                        req.ContentType = "application/json";
                        req.ContentLength = byteData.Length;
                        var putStream = await req.GetRequestStreamAsync().ConfigureAwait(false);
                        await putStream.WriteAsync(byteData, 0, byteData.Length).ConfigureAwait(false);
                    }
                }
                try
                {
                    var resp = await req.GetResponseAsync().ConfigureAwait(false);
                    using (var rs = resp.GetResponseStream())
                    {
                        if (rs == null) return getFromJson(null);
                        using (var sr = new StreamReader(rs))
                        {
                            var result = getFromJson(sr.ReadToEnd());
                            _scheduleCache = null;
                            return result;
                        }
                    }
                }
                catch (WebException e)
                {
                    try
                    {
                        using (var ers = e.Response.GetResponseStream())
                        {
                            if (ers == null) return getFromJson("fail");
                            using (var er = new StreamReader(ers))
                            {
                                e.AddLoggedData("API Response JSON", er.ReadToEnd());
                            }
                        }
                    }
                    catch { /* we gave it a shot, but don't boom in the boom that feeds */ }

                    Current.LogException(
                        e.AddLoggedData("Sent Data", JSON.Serialize(data, JilOptions))
                         .AddLoggedData("Endpoint", fullUri)
                         .AddLoggedData("Headers", req.Headers.ToString())
                         .AddLoggedData("Contecnt Type", req.ContentType));
                    return getFromJson("fail");
                }
            }
        }

        private Cache<List<PagerDutyPerson>> _allusers;
        public Cache<List<PagerDutyPerson>> AllUsers =>
            _allusers ?? (_allusers = GetPagerDutyCache(60.Minutes(),
                    () => GetFromPagerDutyAsync("users/", r => JSON.Deserialize<PagerDutyUserResponse>(r.ToString(), JilOptions).Users))
            );

        public PagerDutyPerson GetPerson(string id) => AllUsers.Data.FirstOrDefault(u => u.Id == id);
    }
}
