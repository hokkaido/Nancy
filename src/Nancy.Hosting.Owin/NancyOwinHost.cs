﻿namespace Nancy.Hosting.Owin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Bootstrapper;
    using Extensions;

    using BodyDelegate = System.Func<System.Func<System.ArraySegment<byte>, // data
                                     System.Action,                         // continuation
                                     bool>,                                 // continuation will be invoked
                                     System.Action<System.Exception>,       // onError
                                     System.Action,                         // on Complete
                                     System.Action>;                        // cancel

    // Holy big-ass delegate signature Batman!
    using ResponseCallBack = System.Action<string, System.Collections.Generic.IDictionary<string, string>, System.Func<System.Func<System.ArraySegment<byte>, System.Action, bool>, System.Action<System.Exception>, System.Action, System.Action>>;

    /// <summary>
    /// Nancy host for OWIN hosts
    /// </summary>
    public class NancyOwinHost
    {
        /// <summary>
        /// State object for async request builder stream begin/endwrite
        /// </summary>
        private sealed class AsyncBuilderState
        {
            public Stream Stream { get; private set; }
            public Action OnComplete { get; private set; }
            public Action<Exception> OnError { get; private set; }

            public AsyncBuilderState(Stream stream, Action onComplete, Action<Exception> onError)
            {
                this.Stream = stream;
                this.OnComplete = onComplete;
                this.OnError = onError;
            }
        }

        private INancyEngine engine;

        public NancyOwinHost() : this(NancyBootstrapperLocator.Bootstrapper)
        {
        }

        public NancyOwinHost(INancyBootstrapper bootstrapper)
        {
            bootstrapper.Initialise();

            this.engine = bootstrapper.GetEngine();
        }

        /// <summary>
        /// OWIN Application Delegate
        /// </summary>
        /// <param name="environment">Application environment</param>
        /// <param name="responseCallBack">Response callback delegate</param>
        /// <param name="errorCallback">Error callback delegate</param>
        public void ProcessRequest(IDictionary<string, object> environment, ResponseCallBack responseCallBack, Action<Exception> errorCallback)
        {
            this.CheckVersion(environment);

            var parameters = environment.AsNancyRequestParameters();

            var requestBodyDelegate = this.GetRequestBodyDelegate(environment);

            // If there's no body, just invoke Nancy immediately
            if (requestBodyDelegate == null)
            {
                this.InvokeNancy(parameters, responseCallBack, errorCallback);
                return;
            }

            // If a body is present, build the RequestStream and 
            // invoke Nancy when it's ready.
            requestBodyDelegate.Invoke(
                this.GetRequestBodyBuilder(parameters, errorCallback), 
                errorCallback, 
                () => this.InvokeNancy(parameters, responseCallBack, errorCallback));
        }

        private void CheckVersion(IDictionary<string, object> environment)
        {
            object version;
            environment.TryGetValue("owin.Version", out version);

            if (version == null || !String.Equals(version.ToString(), "1.0"))
            {
                throw new InvalidOperationException("An OWIN v1.0 host is required");
            }
        }

        private BodyDelegate GetRequestBodyDelegate(IDictionary<string, object> environment)
        {
            return (BodyDelegate)environment["owin.RequestBody"];
        }

        private Func<ArraySegment<byte>, Action, bool> GetRequestBodyBuilder(NancyRequestParameters parameters, Action<Exception> errorCallback)
        {
            return (data, continuation) =>
                {
                    if (continuation == null)
                    {
                        // If continuation is null then we must use sync and return false
                        parameters.Body.Write(data.Array, data.Offset, data.Count);
                        return false;
                    }

                    // Otherwise use begin/end (which may be blocking anyway)
                    // and return true.
                    // No need to do any locking because the spec states we can't be called again
                    // until we call the continuation.
                    var asyncState = new AsyncBuilderState(parameters.Body, continuation, errorCallback);
                    parameters.Body.BeginWrite(
                        data.Array,
                        data.Offset,
                        data.Count,
                        (ar) =>
                        {
                            var state = (AsyncBuilderState)ar.AsyncState;

                            try
                            {
                                state.Stream.EndWrite(ar);

                                state.OnComplete.Invoke();
                            }
                            catch (Exception e)
                            {
                                state.OnError.Invoke(e);
                            }

                            return;
                        },
                        asyncState);

                    return true;
                };
        }

        private void InvokeNancy(NancyRequestParameters parameters, ResponseCallBack responseCallBack, Action<Exception> errorCallback)
        {
            try
            {
                parameters.Body.Seek(0, SeekOrigin.Begin);

                var request = new Request(parameters.Method, parameters.Uri, parameters.Headers, parameters.Body, parameters.Protocol, parameters.Query);

                // Execute the nancy async request handler
                this.engine.HandleRequest(
                    request, 
                    (result) =>
                    {
                        var returnCode = this.GetReturnCode(result);
                        var headers = result.Response.Headers;

                        responseCallBack.Invoke(returnCode, headers, this.GetResponseBodyBuilder(result));
                    }, 
                    errorCallback);
            }
            catch (Exception e)
            {
                errorCallback.Invoke(e);
            }
        }

        private BodyDelegate GetResponseBodyBuilder(NancyContext result)
        {
            return (next, error, complete) =>
                {
                    using (var stream = new ResponseStream(next, complete))
                    {
                        try
                        {
                            result.Response.Contents.Invoke(stream);
                        }
                        catch (Exception e)
                        {
                            error.Invoke(e);
                        }
                    }

                    // Don't support cancelling - should we throw here or just do nothing?
                    return () => { throw new InvalidOperationException("Cancellation is not supported"); };
                };
        }

        private string GetReturnCode(NancyContext result)
        {
            return String.Format("{0} {1}", (int)result.Response.StatusCode, result.Response.StatusCode);
        }
    }
}
