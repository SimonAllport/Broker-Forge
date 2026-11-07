using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Text;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin.Transport
{
    /// <summary>
    /// Posts the canonical request to the host with a Power Platform managed identity token (phase 0
    /// finding 3). The 110-second timeout sits above the host's 90 seconds and below the 2-minute sandbox limit.
    /// </summary>
    public sealed class HostClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(110) };
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        private readonly string _hostUrl;
        private readonly string _scope;
        private readonly IManagedIdentityService _identity;

        public HostClient(string hostUrl, string scope, IManagedIdentityService identity)
        {
            _hostUrl = hostUrl.TrimEnd('/');
            _scope = scope;
            _identity = identity;
        }

        public CanonicalResponse Execute(CanonicalRequest request)
        {
            string token;
            try
            {
                token = _identity.AcquireToken(new[] { _scope });
            }
            catch (Exception ex)
            {
                throw new BrokerUnavailableException("The plug-in could not get a managed identity token for scope " + _scope + ": " + ex.Message, request.CorrelationId, ex);
            }

            using (var message = new HttpRequestMessage(HttpMethod.Post, _hostUrl + "/v1/execute"))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                message.Content = new StringContent(Write(request), Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = Http.SendAsync(message).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is System.Threading.Tasks.TaskCanceledException)
                {
                    throw new BrokerUnavailableException("The BrokerForge host at " + _hostUrl + " did not answer: " + ex.Message, request.CorrelationId, ex);
                }

                using (response)
                {
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                        return Read<CanonicalResponse>(body);

                    ErrorPayload payload = null;
                    try { payload = body.Length > 0 ? Read<ErrorPayload>(body) : null; }
                    catch (Exception) { payload = null; }
                    if (payload == null || string.IsNullOrEmpty(payload.Error))
                        throw new BrokerUnavailableException(string.Format("The BrokerForge host returned HTTP {0} without an error body{1}.",
                            (int)response.StatusCode, (int)response.StatusCode == 401 ? " (it rejected the plug-in identity)" : ""), request.CorrelationId);
                    if (string.IsNullOrEmpty(payload.CorrelationId))
                        payload.CorrelationId = request.CorrelationId;
                    throw ErrorRaiser.ToException(payload);
                }
            }
        }

        private static string Write<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), Settings).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Read<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T), Settings).ReadObject(stream);
        }
    }
}
