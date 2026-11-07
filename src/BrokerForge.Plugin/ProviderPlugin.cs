using System;
using System.Linq;
using BrokerForge.Contracts;
using BrokerForge.Plugin.Translation;
using BrokerForge.Plugin.Transport;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin
{
    /// <summary>
    /// The BrokerForge virtual table data provider, registered as the same type for all five messages.
    /// It turns each message into the canonical request and the reply back into Dataverse output. No
    /// source logic, no descriptor logic and no credentials live here (design principle 5).
    /// </summary>
    public sealed class ProviderPlugin : IPlugin
    {
        // Columns on the bf_brokerdatasource data source table. No secrets: URL, scope and instance name only.
        private const string HostUrlColumn = "bf_hosturl";
        private const string ScopeColumn = "bf_scope";
        private const string ServiceInstanceColumn = "bf_serviceinstance";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var correlationId = context.CorrelationId.ToString();
            tracing.Trace("BrokerForge {0} {1} correlationId={2}", context.MessageName, context.PrimaryEntityName, correlationId);

            try
            {
                var dataSource = DataSource(serviceProvider, correlationId);
                var identity = (IManagedIdentityService)serviceProvider.GetService(typeof(IManagedIdentityService));
                if (identity == null)
                    throw new BrokerUnavailableException("IManagedIdentityService is not available in this context.", correlationId);
                var environmentId = (context as IPluginExecutionContext6)?.EnvironmentId;
                if (string.IsNullOrEmpty(environmentId))
                    throw new BrokerUnavailableException("The execution context has no environment ID.", correlationId);

                var request = new CanonicalRequest
                {
                    CorrelationId = correlationId,
                    EnvironmentId = environmentId,
                    ServiceInstance = Require(dataSource, ServiceInstanceColumn, correlationId),
                    Table = context.PrimaryEntityName,
                    Message = context.MessageName,
                    Caller = new CallerContext
                    {
                        UserObjectId = UserObjectId(context),
                        Async = context.Mode == 1,
                    },
                };
                Translate(context, request, correlationId);

                var client = new HostClient(Require(dataSource, HostUrlColumn, correlationId), Require(dataSource, ScopeColumn, correlationId), identity);
                var response = client.Execute(request);
                tracing.Trace("BrokerForge host replied correlationId={0}", correlationId);
                Output(context, response, correlationId);
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
                // A wire or platform fault (an unreadable host reply, an unexpected input type). The user
                // still gets the correlation ID to quote; the detail goes to the trace log only.
                tracing.Trace("BrokerForge failed correlationId={0}: {1}: {2}", correlationId, ex.GetType().Name, ex.Message);
                throw ErrorRaiser.ToException(new ErrorPayload { Error = "InternalError", Message = "The BrokerForge plug-in failed (" + ex.GetType().Name + ").", CorrelationId = correlationId });
            }
        }

        /// <summary>
        /// The initiating user's Entra object ID, or null when the call is impersonated (HLD D-27). Under
        /// CallerObjectId the platform reports the impersonated user as the initiating user (phase 3 findings 10),
        /// so the ID is sent only when the authenticated user is that same user. Null makes per-user modes fail
        /// with ConnectionRequiredException; service account modes do not use it.
        /// </summary>
        internal static string UserObjectId(IPluginExecutionContext context)
        {
            var authenticated = (context as IPluginExecutionContext3)?.AuthenticatedUserId ?? Guid.Empty;
            if (authenticated == Guid.Empty || authenticated != context.InitiatingUserId)
                return null;
            var id = (context as IPluginExecutionContext2)?.InitiatingUserAzureActiveDirectoryObjectId ?? Guid.Empty;
            return id == Guid.Empty ? null : id.ToString();
        }

        private static Entity DataSource(IServiceProvider serviceProvider, string correlationId)
        {
            var retriever = (IEntityDataSourceRetrieverService)serviceProvider.GetService(typeof(IEntityDataSourceRetrieverService));
            var dataSource = retriever?.RetrieveEntityDataSource();
            if (dataSource == null)
                throw new BrokerUnavailableException("The virtual table has no BrokerForge data source record.", correlationId);
            return dataSource;
        }

        private static void Translate(IPluginExecutionContext context, CanonicalRequest request, string correlationId)
        {
            var inputs = context.InputParameters;
            switch (context.MessageName)
            {
                case Messages.RetrieveMultiple:
                    var query = inputs.Contains("Query") ? inputs["Query"] as QueryExpression : null;
                    if (query == null)
                        throw new UnsupportedQueryException("RetrieveMultiple did not receive a QueryExpression.", correlationId);
                    request.Query = QueryTranslator.Translate(query, correlationId);
                    break;
                case Messages.Retrieve:
                case Messages.Delete:
                    request.Id = ((EntityReference)inputs["Target"]).Id.ToString();
                    break;
                case Messages.Create:
                    var created = (Entity)inputs["Target"];
                    // The source assigns keys and the host encodes them; an id chosen by the caller would be dropped.
                    // Target.Id is no signal: Dataverse fills it on every virtual table Create.
                    if (created.Contains(created.LogicalName + "id"))
                        throw new UnsupportedQueryException("Create cannot take an id: the source assigns the key of a new " + created.LogicalName + " record.", correlationId);
                    request.Record = RecordTranslator.ToCells(created, correlationId);
                    break;
                case Messages.Update:
                    var target = (Entity)inputs["Target"];
                    request.Id = target.Id.ToString();
                    request.Record = RecordTranslator.ToCells(target, correlationId);
                    break;
                default:
                    throw new UnsupportedQueryException("Message " + context.MessageName + " is not handled by the BrokerForge provider.", correlationId);
            }
        }

        private static void Output(IPluginExecutionContext context, CanonicalResponse response, string correlationId)
        {
            switch (context.MessageName)
            {
                case Messages.RetrieveMultiple:
                    var collection = new EntityCollection { EntityName = context.PrimaryEntityName };
                    collection.Entities.AddRange((response.Rows ?? new System.Collections.Generic.List<Row>()).Select(r => RecordTranslator.ToEntity(context.PrimaryEntityName, r)));
                    collection.MoreRecords = response.Next != null;
                    collection.PagingCookie = response.Next;
                    context.OutputParameters["BusinessEntityCollection"] = collection;
                    break;
                case Messages.Retrieve:
                    if (response.Record == null)
                        throw new MappingException("The host returned no record for Retrieve.", correlationId);
                    context.OutputParameters["BusinessEntity"] = RecordTranslator.ToEntity(context.PrimaryEntityName, response.Record);
                    break;
                case Messages.Create:
                    Guid id;
                    if (!Guid.TryParse(response.Id, out id))
                        throw new MappingException("The host returned no id for Create.", correlationId);
                    context.OutputParameters["id"] = id;
                    break;
            }
        }

        private static string Require(Entity entity, string column, string correlationId)
        {
            var value = entity.GetAttributeValue<string>(column);
            if (string.IsNullOrWhiteSpace(value))
                throw new BrokerUnavailableException(string.Format("Data source record {0} has no value for {1}.", entity.Id, column), correlationId);
            return value;
        }
    }
}
