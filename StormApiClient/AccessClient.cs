﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.Threading;
using System.Threading.Tasks;
using System.Web.UI;
using Unity;

using Enferno.Public.Caching;
using Enferno.Public.InversionOfControl;
using Enferno.Public.Logging;
using Enferno.StormApiClient.EndpointBehavior;
using Enferno.StormApiClient.Expose;

namespace Enferno.StormApiClient
{
    public interface IAccessClient : IDisposable
    {
        event EventHandler<EventArgs> ResponseReady;

        Applications.ApplicationService ApplicationProxy { get; }
        Customers.CustomerService CustomerProxy { get; }
        ExposeService ExposeProxy { get; }
        Orders.OrderService OrderProxy { get; }
        Products.ProductService ProductProxy { get; }
        Shopping.ShoppingService ShoppingProxy { get; }

        bool IsCached(string method, params object[] parameters);
        void ProcessRequests();
        Task ProcessRequestsAsync();
        void ProcessRequests(Page page);
        void RegisterRequest(string key, Request request);
        bool TryGet<TR>(string key, out TR result);
        bool UseCache { get; set; }
    }

    /// <summary>
    /// This interface can be used to mock the create of the service clients.
    /// </summary>
    public interface IServiceFactory
    {
        TS CreateProxy<TS, T>(bool useCache, ICacheManager cacheManager, string cacheName, ref T proxy)
            where TS : class
            where T : ClientBase<TS>, TS, new();
    }

    /// <summary>
    ///  ServiceFactory used to create the service factory. Creates  cacheable proxies or a real proxies for the service clients (interfaces).
    /// </summary>
    public class ServiceFactory: IServiceFactory
    {
        public TS CreateProxy<TS, T>(bool useCache, ICacheManager cacheManager, string cacheName, ref T proxy)
            where TS : class
            where T : ClientBase<TS>, TS, new()
        {
            if (proxy == null) proxy = new T();
            return useCache ? CacheableProxy<T, TS>.CreateProxy(cacheManager, cacheName, proxy) : proxy;
        }
    }
    
    public class AccessClient : IAccessClient
    {
        public static readonly string LogCategory = "Enferno.StormApiClient.AccessClient";

        public event EventHandler<EventArgs> ResponseReady;

        private class RequestResponseData
        {
            public Request Request { get; }
            public object Response { get; set; }
            public string CacheKey { get; }
            public bool IsCached { get; set; }

            public RequestResponseData(Request request, object response, string thumbprint)
            {
                Request = request;
                CacheKey = request.GetCacheKey(thumbprint);
                IsCached = (response != null);
                Response = response;
            }
        }

        private ExposeServiceClient exposeProxy;
        private Applications.ApplicationServiceClient applicationProxy;
        private Customers.CustomerServiceClient customerProxy;
        private Orders.OrderServiceClient orderProxy;
        private Products.ProductServiceClient productProxy;
        private Shopping.ShoppingServiceClient shoppingProxy;

        private bool isProcessed;
        private Exception exception;
        private Dictionary<string, RequestResponseData> requests;

        private readonly string cacheName;
        private readonly ICacheManager cacheManager;

        readonly IServiceFactory serviceFactory;

        private bool useCache = true;
        /// <summary>
        /// Set this to false to temporarilly disable caching. When the uncached call is done revert to caching by setting it to true again. True is default.
        /// </summary>
        public bool UseCache { get { return useCache; } set { useCache = value; } }

        /// <summary>
        /// This AccessClient instance should be instantiated as private variable on the page requiring StormAPI access. It serves as a wrapper for the different services provided.
        /// </summary>
        public AccessClient() : this("AccessClient")
        {
        }

        /// <summary>
        /// This AccessClient instance should be instantiated in tests only. Enables injection of a ServiceFactory.
        /// </summary>
        public AccessClient(IServiceFactory factory) : this("AccessClient")
        {
            serviceFactory = factory;
        }

        /// <summary>
        /// This AccessClient instance should be instantiated as private variable on the page requiring StormAPI access. It serves as a wrapper for the different services provided.
        /// </summary>
        public AccessClient(string cacheName)
        {
            serviceFactory = new ServiceFactory();
            isProcessed = false;
            this.cacheName = cacheName;
            cacheManager = CacheManager.Instance;
        }

        public Applications.ApplicationService ApplicationProxy => CreateProxy<Applications.ApplicationService, Applications.ApplicationServiceClient>(ref applicationProxy);

        public Customers.CustomerService CustomerProxy => CreateProxy<Customers.CustomerService, Customers.CustomerServiceClient>(ref customerProxy);

        public Orders.OrderService OrderProxy => CreateProxy<Orders.OrderService, Orders.OrderServiceClient>(ref orderProxy);

        public Products.ProductService ProductProxy => CreateProxy<Products.ProductService, Products.ProductServiceClient>(ref productProxy);

        public Shopping.ShoppingService ShoppingProxy => CreateProxy<Shopping.ShoppingService, Shopping.ShoppingServiceClient>(ref shoppingProxy);

        public ExposeService ExposeProxy => CreateProxy<ExposeService, ExposeServiceClient>(ref exposeProxy);

        private TS CreateProxy<TS, T>(ref T proxy) where TS: class where T : ClientBase<TS>, TS, new()
        {
            var retval = serviceFactory.CreateProxy<TS, T>(UseCache, cacheManager, cacheName, ref proxy);
            if(proxy != null) CheckAndSetCertificateFromFile(proxy.ClientCredentials);
           
            var httpHeaders = new Dictionary<string, string>();
            httpHeaders.Add("user-agent", "Storm-API-Client: " + typeof(AccessClient).AssemblyQualifiedName);

            proxy.Endpoint.Behaviors.Add(new HttpHeadersEndpointBehavior(httpHeaders));

            return retval;
            
        }

        private void CheckAndSetCertificateFromFile(ClientCredentials clientCredentials)
        {
            try
            {
                if (clientCredentials == null || clientCredentials.ClientCertificate.Certificate != null)
                {
                    if (clientCredentials == null) Log.LogEntry.Categories(LogCategory).Categories(CategoryFlags.Debug).Message("CheckAndSetCertificateFromFile: No clientCredentials.").WriteVerbose();
                    else Log.LogEntry.Categories(LogCategory).Categories(CategoryFlags.Debug).Message("CheckAndSetCertificateFromFile: Using existing certificate.").Property("Certificate", clientCredentials.ClientCertificate.Certificate.FriendlyName).WriteVerbose();

                    return;
                }

                var certResolver = IoC.Resolve<ICertificateResolver>();
                var certificate = certResolver.GetCertificate();
                if(certificate != null) clientCredentials.ClientCertificate.Certificate = certificate;
                else Log.LogEntry.Categories(LogCategory).Categories(CategoryFlags.Debug).Message("CheckAndSetCertificateFromFile: Could not find certificate.").WriteWarning();
            }
            catch (Exception ex)
            {
                Log.LogEntry.Categories(LogCategory).Categories(CategoryFlags.Alert)
                    .Property("Certificate", clientCredentials?.ClientCertificate.Certificate?.ToString())
                    .Message("Failed CheckAndSetCertificateFromFile.").Exceptions(ex).WriteError();

                throw;
            }
        }

        private List<RequestResponseData> UncachedData { get { return requests.Where(d => !d.Value.IsCached).Select(d => d.Value).ToList(); } }

        private RequestList RequestList 
        { 
            get
            {
                var list = new RequestList();
                list.AddRange(UncachedData.Where(d => !d.IsCached).Select(d => d.Request));
                return list;
            }
        }

        /// <summary>
        /// RegisterRequest should be called no later than in PreRender in the Page life cycle. And not after ProcessRequests.
        /// </summary>
        /// <param name="key">The name of the response object retrieved later in the page life cycle.</param>
        /// <param name="request">The acctual request object</param>
        public void RegisterRequest(string key, Request request)
        {
            if (request == null) return;
            object response;
            if (IsCached(request, out response))
            {
                Log.LogEntry.Categories(CategoryFlags.Debug).Message("{0} is cached. Thread: [{1}]", key, Thread.CurrentThread.ManagedThreadId).WriteVerbose();
            }
                
            if (requests == null)
            {
                requests = new Dictionary<string, RequestResponseData>();
            }

            requests.Add(key, new RequestResponseData(request, response, GetCertificateThumbprint()));
        }

        public bool IsCached(string method, params object[] parameters)
        {
            object res;
            string key = cacheManager.GetKey(method, PrependCertificateThumbprint(parameters));

            if (!useCache) return false;
            if (!cacheManager.HasConfiguration(cacheName)) return false;
            return cacheManager.TryGet(cacheName, key, out res);
        }

        private string GetCertificateThumbprint()
        {
            if (exposeProxy == null)
            {
// ReSharper disable once UnusedVariable
                var justARefToCreateProxy = ExposeProxy.GetType();
            }
            return exposeProxy?.ClientCredentials?.ClientCertificate.Certificate?.Thumbprint;
        }

        private object[] PrependCertificateThumbprint(object[] args)
        {
            var thumbprint = GetCertificateThumbprint();
            return thumbprint != null ? new object[] { thumbprint }.Union(args).ToArray() : args;
        }

        /// <summary>
        /// TryGet tries to get the response from a previous registered request. This should not be attempted before PreRenderComplete in the page life cycle.
        /// </summary>
        /// <typeparam name="TR">The Response objects type. If the request was for GetApplicationRequest then the response type would be GetApplicationResponse</typeparam>
        /// <param name="key">The key used for the request.</param>
        /// <param name="result">The acctual response object instance</param>
        /// <returns>True if the response for the given key was found. False otherwise.</returns>
        public bool TryGet<TR>(string key, out TR result)
        {
            if (!isProcessed) ProcessRequests();
            CheckException(key);

            result = default(TR);

            RequestResponseData data;
            if (requests == null || !requests.TryGetValue(key, out data)) return false;

            AddToCache(key, data);

            result = (TR)data.Response;
            return true;
        }

        private void CheckException(string key)
        {
            if (exception != null) throw new ApplicationException($"Failed to get key {key} from result. See inner exception for details.", exception);
        }

        private void CheckException()
        {
            if (exception != null) throw new ApplicationException("Failed to get data from result. See inner exception for details.", exception);
        }

        private bool IsCached<TR>(Request request, out TR response)
        {
            response = default(TR);
            if (!useCache) return false;
            if (!cacheManager.HasConfiguration(cacheName)) return false;

            return cacheManager.TryGet(cacheName, request.GetCacheKey(GetCertificateThumbprint()), out response);
        }

        private void AddToCache(string key, RequestResponseData data)
        {
            if (!useCache) return;
            if (data.IsCached || !cacheManager.HasConfiguration(cacheName)) return;
            if (cacheManager.Add(cacheName, data.CacheKey, data.Response))
            {
                data.IsCached = true;
                Log.LogEntry.Categories(CategoryFlags.Debug).Message("Adding {0} to cache. Thread: [{1}]", key, Thread.CurrentThread.ManagedThreadId).WriteVerbose();
            }
        }

        /// <summary>
        /// ProcessRequests processes all registered requests. The responses will be returned synchronously.
        /// </summary>
        public void ProcessRequests()
        {
            ProcessRequests(null);
        }

        /// <summary>
        /// ProcessRequests processes all registered requests. If the page is asynchronous then the requests will be submitted asynchronously. Otherwise the responses will be returned synchronously.
        /// </summary>
        /// <param name="page">The page instance on which this instance is defined.</param>
        public void ProcessRequests(Page page)
        {
            if (NothingToProcess)
            {
                isProcessed = true;
                if (AllIsCached)
                {
                    OnResponseReady();
                }            
                return;
            }

            isProcessed = true;
            if (page != null && page.IsAsync)
            {
                Log.LogEntry.Categories(CategoryFlags.Debug).Message("Processing async request. Thread: [{0}]", Thread.CurrentThread.ManagedThreadId).WriteVerbose();
                page.RegisterAsyncTask(new PageAsyncTask(AsyncTask));
            }
            else
            {
                Log.LogEntry.Categories(CategoryFlags.Debug).Message("Processing sync request. Thread: [{0}]", Thread.CurrentThread.ManagedThreadId).WriteVerbose();

                try
                {
                    var responses = ExposeProxy.Process(RequestList);
                    PopulateResponses(responses);
                }
                catch (Exception ex) { exception = ex; OnResponseReady(); }
            }
        }

        /// <summary>
        /// ProcessRequests processes all registered requests. The responses will be returned synchronously.
        /// </summary>
        public async Task ProcessRequestsAsync()
        {
            var responses = ExposeProxy.ProcessAsync(RequestList);
            PopulateResponses(await responses);
        }

        private bool AllIsCached => requests != null && RequestList.Count == 0;

        private bool NothingToProcess => isProcessed || requests == null || requests.Count == 0 || RequestList.Count == 0;

        private async Task AsyncTask()
        {
            try
            {
                var data = await ExposeProxy.ProcessAsync(RequestList);
                PopulateResponses(data);
            }
            catch (Exception ex) { exception = ex; OnResponseReady(); }
        }

        private void PopulateResponses(ResponseList responses)
        {
            for (var i = 0; i < responses.Count; i++)
            {
                UncachedData[i].Response = responses[i].GetResult();
            }
            ClearRedirectedCacheEntries();
            OnResponseReady();
        }

        private void ClearRedirectedCacheEntries()
        {
            foreach (var request in requests)
            {
                cacheManager.ClearRedirected(cacheName, request.Value.CacheKey, request.Value.Response);
            }
        }

        protected void OnResponseReady()
        {
            CheckException();
            ResponseReady?.Invoke(this, new EventArgs());
        }


        private bool disposed;
        ~AccessClient()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose should be called in the Unload of the page.
        /// </summary>
        private void Dispose(bool disposing)
        {
            if (disposed) return;

            if (disposing)
            {
                Cleanup(exposeProxy);
                Cleanup(applicationProxy);
                Cleanup(customerProxy);
                Cleanup(orderProxy);
                Cleanup(productProxy);
                Cleanup(shoppingProxy);
            }
            disposed = true;
        }

        private static void Cleanup(ICommunicationObject proxy)
        {
            if (proxy == null || proxy.State == CommunicationState.Closed) return;

            Log.LogEntry.Categories(CategoryFlags.Debug).Message("Disposing {0}. Thread: [{1}]", proxy.GetType(), Thread.CurrentThread.ManagedThreadId).WriteVerbose();

            if (proxy.State == CommunicationState.Faulted) proxy.Abort();
            else proxy.Close();
        }
    }
}
