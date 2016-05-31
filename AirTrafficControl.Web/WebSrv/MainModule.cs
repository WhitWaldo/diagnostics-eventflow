﻿using AirTrafficControl.Interfaces;
using AirTrafficControl.Web.Fabric;
using AirTrafficControl.Web.TrafficSimulator;
using Nancy;
using Nancy.ModelBinding;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Validation;
using System.Diagnostics;

namespace AirTrafficControl.Web.WebSrv
{
    public class MainModule: NancyModule
    {
        private delegate Task<Response> AsyncNancyRequestHandler(dynamic parameters, CancellationToken cancellationToken);

        private const string NotifyFlightStatusUpdate = "FlightStatusUpdate";

        public MainModule()
        {
            try
            {
                Get["/"] = parameters =>
                {
                    return View["atcmain.html", new MainPageModel(Request)];
                };

                Get["/api/airplanes", runAsync: true] = (p, ct) => PerformRestOperation("GetAirplanes", p, ct, (AsyncNancyRequestHandler) (
                    async (parameters, cancellationToken) =>
                    {
                        var atc = new AtcController();
                        var airplaneStates = await atc.GetFlyingAirplaneStates();
                        return Response.AsJson<IEnumerable<AirplaneStateDto>>(airplaneStates).WithHeaders(PublicShortLived());
                    }));
                    

                Get["/api/airplanes/{id}", runAsync: true] = (p, ct) => PerformRestOperation("GetAirplaneState", p, ct, (AsyncNancyRequestHandler) (
                    async (parameters, cancellationToken) =>
                    {
                        DynamicDictionary dynamicParameters = (DynamicDictionary)parameters;
                        dynamic id;                        
                        if (!dynamicParameters.TryGetValue("id", out id) || string.IsNullOrWhiteSpace(id))
                        {
                            return StatusCodeResponse(HttpStatusCode.BadRequest, "Required parameter 'id' is missing or empty");
                        }
                        AtcController atc = new AtcController();
                        AirplaneActorState airplaneState = await atc.GetAirplaneState(id);
                        return Response.AsJson<AirplaneActorState>(airplaneState).WithHeaders(PublicShortLived());
                    }));

                Get["/api/airports", runAsync: true] = (p, ct) => PerformRestOperation("GetAirports", p, ct, (AsyncNancyRequestHandler) (
                    async (parameters, cancellationToken) =>
                    {
                        var atc = new AtcController();
                        var airports = await atc.GetAirports();
                        return Response.AsJson<IEnumerable<Airport>>(airports);
                    }));

                Post["/api/flights", runAsync: true] = (p, ct) => PerformRestOperation("StartNewFlight", p, ct, (AsyncNancyRequestHandler) (
                    async (parameters, cancellationToken) =>
                    {
                        var requestModel = this.Bind<FlightPlanRequestModel>();
                        var atc = new AtcController();
                        await atc.StartNewFlight(requestModel.AirplaneID, requestModel.DepartureAirport.Name, requestModel.DestinationAirport.Name);
                        return HttpStatusCode.Created;
                        // If the flight was addressable individually, we would return something like this:
                        // return new Response(){StatusCode = HttpStatusCode.Created}.WithHeader("Location", "new flight URL");
                    }));

                Post["/api/notify/flight-status", runAsync: true] = (p, ct) => PerformRestOperation("FlightStatusUpdate", p, ct, (AsyncNancyRequestHandler) (
                    async (parameters, cancellationToken) => 
                    {
    #if DEBUG
                        string requestBody = (new StreamReader(this.Request.Body)).ReadToEnd();
    #endif
                        var flightStatusModel = this.Bind<FlightStatusModel>();
                        var atc = new AtcController();
                        await atc.PerformFlightStatusUpdate(flightStatusModel);
                        return HttpStatusCode.NoContent; // Success, just nothing to report back.
                    }));

                Post["/api/simulation/traffic", runAsync: true] = (p, ct) => PerformRestOperation("SimulatedTrafficUpdate", p, ct, (AsyncNancyRequestHandler)(
                    async (parameters, cancellationToken) => 
                    {
                        var model = this.Bind<TrafficSimulationModel>();                        
                        await TrafficSimulator.ChangeTrafficSimulation(model);
                        return HttpStatusCode.NoContent;
                    }));
            }
            catch(Exception e)
            {
                ServiceEventSource.Current.RestApiInitializationError(e.ToString());
                throw;
            }
        }

        private object[] PublicShortLived()
        {
            return new[] { new { Header = "Cache-Control", Value = "public,max-age=1"} };
        }

        private async Task<Response> PerformRestOperation(string operationName, dynamic parameters, CancellationToken cancellationToken, AsyncNancyRequestHandler inner)
        {
            Requires.NotNullOrWhiteSpace(operationName, nameof(operationName));
            Assumes.NotNull(ServiceContext);

            string correlationId = Guid.NewGuid().ToString();
            ServiceEventSource.Current.RestApiOperationStart(operationName, correlationId);
            bool unexpectedException = false;
            DateTime startTimeUtc = DateTime.UtcNow;
            Response retval = null;
            try
            {
                retval = await inner(parameters, cancellationToken).ConfigureAwait(false);
                return retval;
            }
            catch(Exception e) 
            {
                unexpectedException = true;

                ServiceEventSource.Current.RestApiOperationStop(
                    operationName,
                    ServiceContext,
                    correlationId,
                    startTimeUtc,
                    DateTime.UtcNow - startTimeUtc,
                    HttpStatusCode.InternalServerError.ToString(),
                    e.ToString());
                throw; 
            }
            finally
            {
                if (!unexpectedException)
                {
                    Debug.Assert(retval != null);

                    ServiceEventSource.Current.RestApiOperationStop(
                        operationName,
                        ServiceContext,
                        correlationId,
                        startTimeUtc,
                        DateTime.UtcNow - startTimeUtc,
                        retval.StatusCode.ToString()
                        );
                }
            }
        }

        private Response StatusCodeResponse(HttpStatusCode statusCode, string reason)
        {
            Response r = new Response();
            r.StatusCode = statusCode;
            r.ReasonPhrase = reason;
            return r;
        }

        private StatefulServiceContext ServiceContext
        {
            get { return FabricContext<StatefulServiceContext, ITrafficSimulator>.Current.ServiceContext; }
        }

        private ITrafficSimulator TrafficSimulator
        {
            get { return FabricContext<StatefulServiceContext, ITrafficSimulator>.Current.ServiceInstance; }
        }
    }
}
