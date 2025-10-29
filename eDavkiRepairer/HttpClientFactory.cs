using System.Security.Cryptography.X509Certificates;

public static partial class Program
{
    internal class HttpClientFactory : IHttpClientFactory
    {
        private readonly Func<X509Certificate2> _factory;

        public HttpClientFactory(Func<X509Certificate2> factory)
        {
            _factory = factory;
        }

        public HttpClient CreateClient(string name)
        {
            var handler = new HttpClientHandler();
            var certificate = _factory();
            if (certificate is not null)
            {
                handler.ClientCertificates.Add(certificate);
            }
            return new HttpClient(handler);
        }
    }
}