using System;
using System.Collections.Generic;
using BrokerForge.Contracts;
using BrokerForge.Plugin.Translation;
using BrokerForge.Plugin.Transport;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin
{
    /// <summary>
    /// The handler for every BrokerForge unbound Custom API (HLD D-18, D-19). The message name is the Custom
    /// API's unique name; the host finds the service instance from the descriptor that binds it. A Custom API
    /// has no data source record, so the host URL and scope come from the bf_HostUrl and bf_HostScope
    /// environment variables (D-20). No source logic, no descriptor logic and no credentials live here.
    /// </summary>
    public sealed class CustomApiPlugin : IPlugin
    {
        public const string HostUrlVariable = "bf_HostUrl";
        public const string ScopeVariable = "bf_HostScope";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var correlationId = context.CorrelationId.ToString();
            tracing.Trace("BrokerForge Custom API {0} correlationId={1}", context.MessageName, correlationId);

            try
            {
                var identity = (IManagedIdentityService)serviceProvider.GetService(typeof(IManagedIdentityService));
                if (identity == null)
                    throw new BrokerUnavailableException("IManagedIdentityService is not available in this context.", correlationId);
                var environmentId = (context as IPluginExecutionContext6)?.EnvironmentId;
                if (string.IsNullOrEmpty(environmentId))
                    throw new BrokerUnavailableException("The execution context has no environment ID.", correlationId);
                var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                if (factory == null)
                    throw new BrokerUnavailableException("IOrganizationServiceFactory is not available in this context.", correlationId);
                var variables = Variables(factory.CreateOrganizationService(null), correlationId);

                var request = new CanonicalRequest
                {
                    CorrelationId = correlationId,
                    EnvironmentId = environmentId,
                    Message = context.MessageName,
                    Caller = new CallerContext { UserObjectId = ProviderPlugin.UserObjectId(context), Async = context.Mode == 1 },
                    Inputs = ParameterTranslator.ToInputs(context.InputParameters, correlationId),
                };
                var response = new HostClient(variables[HostUrlVariable], variables[ScopeVariable], identity).Execute(request);
                tracing.Trace("BrokerForge host replied correlationId={0}", correlationId);
                ParameterTranslator.SetOutputs(context.OutputParameters, response.Outputs, correlationId);
            }
            catch (BrokerForgeException ex)
            {
                tracing.Trace("BrokerForge failed correlationId={0}: {1}", correlationId, ex.GetType().Name);
                throw ErrorRaiser.ToException(ex);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace("BrokerForge failed correlationId={0}: {1}: {2}", correlationId, ex.GetType().Name, ex.Message);
                throw ErrorRaiser.ToException(new ErrorPayload { Error = "InternalError", Message = "The BrokerForge Custom API handler failed (" + ex.GetType().Name + ").", CorrelationId = correlationId });
            }
        }

        /// <summary>Both variables in one query: the current value where one is set, otherwise the default value.</summary>
        private static Dictionary<string, string> Variables(IOrganizationService organization, string correlationId)
        {
            var query = new QueryExpression("environmentvariabledefinition") { ColumnSet = new ColumnSet("schemaname", "defaultvalue") };
            query.Criteria.AddCondition("schemaname", ConditionOperator.In, HostUrlVariable, ScopeVariable);
            var value = query.AddLink("environmentvariablevalue", "environmentvariabledefinitionid", "environmentvariabledefinitionid", JoinOperator.LeftOuter);
            value.EntityAlias = "v";
            value.Columns = new ColumnSet("value");

            var found = new Dictionary<string, string>();
            foreach (var row in organization.RetrieveMultiple(query).Entities)
            {
                var current = row.GetAttributeValue<AliasedValue>("v.value")?.Value as string;
                var text = string.IsNullOrWhiteSpace(current) ? row.GetAttributeValue<string>("defaultvalue") : current;
                if (!string.IsNullOrWhiteSpace(text))
                    found[row.GetAttributeValue<string>("schemaname")] = text;
            }
            foreach (var name in new[] { HostUrlVariable, ScopeVariable })
                if (!found.ContainsKey(name))
                    throw new BrokerUnavailableException("Environment variable " + name + " has no value, so the BrokerForge host cannot be reached.", correlationId);
            return found;
        }
    }
}
