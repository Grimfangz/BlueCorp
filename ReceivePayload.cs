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

namespace ReceiveAndQueue
{
    public class ReceivePayload
    {
        private readonly ILogger<ReceivePayload> _logger;
        private readonly IConfiguration _config;

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

        private int GetControlNumberFromPayload(string payload){
            using var doc = JsonDocument.Parse(payload);
            doc.RootElement.TryGetProperty("controlNumber", out var controlNum);
            _logger.LogInformation($"ControlNumber: " + controlNum.GetInt32());
            return controlNum.GetInt32();
        }

        [Function(nameof(ReceivePayload))]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            // TODO: Implement Auth via Azure aad
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

                // Check control number
                int payloadControlNumber = GetControlNumberFromPayload(requestBody);
                // TODO: compare this against db value to prevent enque duplicates?

                var storageConnection = _config["AzureWebJobsStorage"];
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
