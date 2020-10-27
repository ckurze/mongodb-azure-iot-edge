using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EdgeHub;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Functions.Samples
{
    public static class mongoFunction
    {
        /* More info on connection management in Azure functions: https://docs.microsoft.com/en-us/azure/azure-functions/manage-connections */
        private static Lazy<MongoClient> lazyMongoClient = new Lazy<MongoClient>(InitializeMongoClient);
        private static MongoClient mongoClient => lazyMongoClient.Value;

        private static MongoClient InitializeMongoClient()
        {
            // Perform any initialization here
            return new MongoClient("mongodb://mongodb:27017");
        }
        
        [FunctionName("mongoFunction")]
        public static async Task FilterMessageAndSendMessage(
                    [EdgeHubTrigger("input1")] Message messageReceived,
                    [EdgeHub(OutputName = "output1")] IAsyncCollector<Message> output,
                    ILogger logger)
        {
            byte[] messageBytes = messageReceived.GetBytes();
            var messageString = System.Text.Encoding.UTF8.GetString(messageBytes);

            if (!string.IsNullOrEmpty(messageString))
            {
                logger.LogInformation("Info: Received one non-empty message");
                using (var pipeMessage = new Message(messageBytes))
                {
                    foreach (KeyValuePair<string, string> prop in messageReceived.Properties)
                    {
                        pipeMessage.Properties.Add(prop.Key, prop.Value);
                    }
                    await output.AddAsync(pipeMessage);
                    StoreMessageToMongoDB(pipeMessage, logger);
                    
                    logger.LogInformation("Info: Piped out the message");
                }
            }
        }

        private static void StoreMessageToMongoDB(Message message, ILogger logger) {
            IMongoCollection<BsonDocument>  mongoCollection = mongoClient.GetDatabase("sample_iot").GetCollection<BsonDocument> ("timeseries");
            try {
                /* 
                Note: 
                There are well-documented best practices for modelling time series data in MongoDB, please find them documented 
                in a whitepaper (https://www.mongodb.com/collateral/time-series-best-practices) as well as 
                code examples (https://github.com/ckurze/mongodb-iot-reference/tree/master/mongodb-timeseries). 
                
                For simplicity, we do not follow these patterns here, as we also do not expect high volumes of data. 
                */
                mongoCollection.InsertOne(BsonDocument.Parse(Encoding.UTF8.GetString(message.GetBytes())));
                logger.LogInformation("Info: Stored the message to MongoDB");
            }
            catch(Exception e) {
                logger.LogError("Error writing to MongoDB: " + e.Message);
            }     
        }
    }
}
