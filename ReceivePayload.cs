using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Azure.Data.Tables;
using Azure;

namespace ReceiveAndQueue
{
    public class ReceivePayload
    {
        private readonly ILogger<ReceivePayload> _logger;
        private readonly IConfiguration _config;
        private TableClient _tableClient;

        public ReceivePayload(ILogger<ReceivePayload> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        private bool IsValidPayload(string payload){
            // Max 800kb ? not sure if we need to verify this
            var schemaFile = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload_schema");
            StreamReader reader = new StreamReader(schemaFile);
            string schemaText = reader.ReadToEnd();
            JSchema schema = JSchema.Parse(schemaText);
            try{
                JObject json = JObject.Parse(payload);
                bool isValid = json.IsValid(schema, out IList<string> errors);
                _logger.LogInformation($"Payload is valid: " + isValid);
                return isValid;
            }
            catch (Exception e){
                _logger.LogInformation("Error with parsing payload json");
                throw e;
            }
        }

        public async Task AddDefaultEntityIfNotExistsAsync(TableClient tableClient, string partitionKey, string rowKey)
        {
            try
            {
                await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey);
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                var defaultEntity = new TableEntity(partitionKey, rowKey)
                {
                    { "Value", 0 } 
                };

                await tableClient.AddEntityAsync(defaultEntity);
            }
        }

        private int GetControlNumberFromPayload(string payload){
            using var doc = JsonDocument.Parse(payload);
            doc.RootElement.TryGetProperty("controlNumber", out var controlNum);
            _logger.LogInformation($"ControlNumber: " + controlNum.GetInt32());
            return controlNum.GetInt32();
        }

        [Function(nameof(ReceivePayload))]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            _logger.LogInformation("Locked and Loaded");
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            try{
                if (!IsValidPayload(requestBody)){
                    _logger.LogInformation("Failed to validate against schema");
                    response = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                    await response.WriteStringAsync("Payload failed schema validation");
                    return response;
                }
                var storageConnection = _config["AzureWebJobsStorage"];

                // Check control number
                int payloadControlNumber = GetControlNumberFromPayload(requestBody);

                _tableClient = new TableClient(storageConnection, "ControlCount");
                await _tableClient.CreateIfNotExistsAsync();
                await AddDefaultEntityIfNotExistsAsync(_tableClient, "ControlPartition", "ControlNumber");

                var result = await _tableClient.GetEntityAsync<TableEntity>("ControlPartition", "ControlNumber");// "ControlNumber is the name of the RowKey
                var entity = result.Value;

                int storedControlNumber = Convert.ToInt32(entity["Value"]);
                if (!(payloadControlNumber > storedControlNumber)){
                    response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                    _logger.LogInformation($"Control Number is duplicate or outdated: {payloadControlNumber}");
                    await response.WriteStringAsync("Payload control number is duplicate or oudated, will not enqueue");
                    return response;
                }
                entity["Value"] = payloadControlNumber;
                await _tableClient.UpdateEntityAsync(entity, result.Value.ETag, TableUpdateMode.Replace);
                _logger.LogInformation($"Control Number has been updated: {payloadControlNumber}");

                // Add to Queue
                var queueName = "dispatch-processing-queue";
                var queueClient = new QueueClient(storageConnection, queueName);
                await queueClient.CreateIfNotExistsAsync();
                await queueClient.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(requestBody)));
                _logger.LogInformation("Added to queue");

                response = req.CreateResponse(System.Net.HttpStatusCode.OK);
                await response.WriteStringAsync("Load Received and Enqueued");
                return response;
            } 
            catch (Exception e){
                _logger.LogError(e.Message);
                response = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Failed to process Payload");
                return response;
            }
        }
    }
}
