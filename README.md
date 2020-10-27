# Using MongoDB Server on Edge Devices via Azure IoT Edge

I frequently got asked how MongoDB Server can be used in combination with Azure IoT Edge. The following tutorial is based on the [Azure SQL Server Quickstart](https://docs.microsoft.com/en-us/azure/iot-edge/tutorial-store-data-sql-server) example, but uses [MongoDB Server](https://www.mongodb.com/) in order to have consistent development experience when using [MongoDB Atlas](https://atlas.mongodb.com/) on Azure in the Cloud. In addition, we can natively work with the JSON data that is generated in the example.

This example deploys a MongoDB Server into the Edge. Another solution would be to use MongoDB Realm as a local datastore. Please refer to the [MongoDB Realm Tutorials, e.g. for .NET](https://docs.mongodb.com/realm/dotnet/) which explains how to use the Realm SDK - it can be easily used to persist data.

## Prerequisites

The following prerequisites should be met:
* Azure Login
* Azure CLI available, incl. Azure IoT Extensions (can be installed via `az extension add --name azure-iot`) - as an alternative, use the Azure Cloud Shell.
* Visual Studio Code with Azure IoT extensions installed
* Local Docker installation for development

For additional details, please see the [Azure IoT Edge documentation and tutorials](https://docs.microsoft.com/en-us/azure/iot-edge/) to get started.

## Create Azure IoT Resources

The following steps are based on the [Tutorial: Store data at the edge with SQL Server databases](https://docs.microsoft.com/en-us/azure/iot-edge/tutorial-store-data-sql-server). Instead of SQL Server, we leverage MongoDB Server.

### Create Resource Group, IoT Hub, Edge Device, and Virtual Machine

The following steps deploy a Resource Group, IoT Hub, Edge Device and a VM that hosts our Edge Device:
```bash
# Login to Azure
az login

# Create Resource Group
az group create --name MongoDBEdgeResources --location westus2

# Create IoT Hub (free instance, pls. change the name in case of conflicts)
az iot hub create --resource-group MongoDBEdgeResources --name MongoDB-IoTHub --sku F1 --partition-count 2

# Show the connection string of the new IoT Hub (take a note, we need it later)
az iot hub connection-string show  

# Create an edge device
az iot hub device-identity create --device-id mongodbEdgeDevice --edge-enabled --hub-name MongoDB-IoTHub

# Show connection string for edge device (take a note, we need it later)
az iot hub device-identity connection-string show --device-id mongodbEdgeDevice --hub-name MongoDB-IoTHub

# Deploy a VM that simulates our Edge Device
az deployment group create --resource-group MongoDBEdgeResources --template-uri "https://aka.ms/iotedge-vm-deploy" --parameters dnsLabelPrefix='mongodb-edge-vm' --parameters adminUsername='azureUser' --parameters deviceConnectionString=$(az iot hub device-identity connection-string show --device-id mongodbEdgeDevice --hub-name MongoDB-IoTHub -o tsv) --parameters authenticationType='password' --parameters adminPasswordOrKey="MongoDB#2020"
```

Test the VM and configure storage:
```bash
# Connect to VM (using the password defined above)
ssh azureUser@mongodb-edge-vm.westus2.cloudapp.azure.com

# Check status of IoT Edge (wait a minute or so until the service starts, status has to show "active (running)")
sudo systemctl status iotedge

# Logs
journalctl -u iotedge

# Running containers
sudo iotedge list
```

We want to persist data from the containers to be robust in case of system restarts. Therefore, some directories need to be created with sufficient privileges to write from containers (also see the [tutorial by Azure](https://docs.microsoft.com/en-us/azure/iot-edge/how-to-store-data-blob#granting-directory-access-to-container-user-on-linux) for more details on privileges):

```bash
# SSH into the VM
ssh azureUser@mongodb-edge-vm.westus2.cloudapp.azure.com

# Create directories and restrict access
sudo mkdir -p /srv/containerdata
sudo chown -R 11000:11000 /srv/containerdata
sudo chmod -R 700 /srv/containerdata
```

### Create an Azure Container Registry

We could also use the Docker registry, but want to focus on Azure products here. Create a new container registry in your resource group with the following settings via the Azure Console:
* Name: MongoDBRegistry
* Location: West US 2
* SKU: Basic

We need to enable an admin user so we can easily upload our containers:
* Open the newly create container registry and choose "Access keys" from the left-hand menu
* Enable the slider "Admin user"
* Take a note of the login server, the username, and the first password (you need it later on)

### Upload a MongoDB Container Image to the Container Registry

We leverage pre-defined MongoDB images for this example. Please note that for production workloads, MongoDB Enterprise is recommended as it also offers capabilities like Encryption at Rest. *Always follow the guidelines outlined in [MongoDB's security checklist](https://docs.mongodb.com/manual/administration/security-checklist/).* There is a [Tutorial how to deploy MongoDB Enterprise via Docker](https://docs.mongodb.com/manual/tutorial/install-mongodb-enterprise-with-docker/) available.

*Note:* The Docker image used here contains all binaries of MongoDB. It would be recommended to limit to the MongoDB Server in order to reduce the size of the image.

Copy the pre-baked Docker image to Azure Container Registry (using your credentials as created above):
```bash
# Login
docker login -u mongodbregistry -p YOUR_PASSWORD mongodbregistry.azurecr.io
az acr login -n mongodbregistry.azurecr.io

# Import a MongoDB Docker image to Azure registry:
az acr import \
  --name mongodbregistry \
  --source docker.io/library/mongo:4.4.1 \
  --image mongo:4.4.1
```

## Create an Azure Function Solution

Please follow the steps outlined in the [Azure Tutorial](https://docs.microsoft.com/en-us/azure/iot-edge/tutorial-store-data-sql-server). The following changes are implemented to use MongoDB instead of SQL Server.

Create a new Azure IoT Solution (Via the Command Pallete > "Azure IoT Edge: New IoT Edge solution"):

* Solution Name: MongoDBAzureEdgeSolution
* Module Template: Azure Functions - C#
* Module Name: mongoFunction
* Docker image repository for the module: mongodbregistry.azurecr.io/mongofunction

### Add the MongoDB Container

Add the MongoDB container via the command palette: "Azure IoT Edge: Add IoT Edge module":
* Choose the `deployment.template.json` manifest file
* Module template: `Existing Module (Import from ACR)`
* Module name: `mongodb` (will be referenced as connection string later on via this name)
* Azure Container Registry: `mongodbregistry.azurecr.io`
* Repository: `mongo`
* Tag: `4.4.1` (as imported above)

We want the data to be persisted, so we map the storage of MongoDB to files on the underlying host in the `HostConfig`. In additon, the `startupOrder` has been introduced for the modules to ensure a proper startup of the system. The `modules` section of the target `deployment.template.json` file should look like this:

```json
"modules": {
  "mongoFunction": {
    "version": "1.0",
    "type": "docker",
    "status": "running",
    "restartPolicy": "always",
    "startupOrder": 2,
    "settings": {
      "image": "${MODULES.mongoFunction}",
      "createOptions": {}
    }
  },
  "SimulatedTemperatureSensor": {
    "version": "1.0",
    "type": "docker",
    "status": "running",
    "restartPolicy": "always",
    "startupOrder": 1,
    "settings": {
      "image": "mcr.microsoft.com/azureiotedge-simulated-temperature-sensor:1.0",
      "createOptions": {}
    }
  },
  "mongodb": {
    "version": "1.0",
    "type": "docker",
    "status": "running",
    "restartPolicy": "always",
    "startupOrder": 0,
    "settings": {
      "image": "mongodbregistry.azurecr.io/mongo:4.4.1",
      "HostConfig": {
        "Binds":["/srv/containerdata:/data/db"]
      },
      "createOptions": {}
    }
  }
}

```

### Update the module with custom code

First, we need to add the MongoDB C# driver (easiest way is to use the NuGet Package Manager):
* Command Palette > "NuGet Package Manager: Add Package"
* Two Packages (in the lastest versions, by the time of writing it is 2.11.3):
  * MongoDB.Bson
  * MongoDB.Driver

The final implementation looks as following `mongoFunction.cs`. It leverages the best practices to reuse database connections by different function exectuions as outlined in the [documentation](https://docs.microsoft.com/en-us/azure/azure-functions/manage-connections).

```cs
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
```

Build and push your solution.

Select the IoT Hub in Visual Studio Code. Select the Edge Device and create a deployment. You should have the following modules now on your edge device:
* $edgeAgent
* $edgeHub
* mongoFunction
* mongodb
* SimulatedTemperatureSensor

Give the VM some time to download and start the docker images. Per default, the simulated temperature sensor will create 500 sensor readings (once every five seconds).

You can start to monitor the built-in event endpoint of your edge device. You will see the frequent messages from the sensor:

```json
{
  "machine": {
    "temperature": 32.02848546930053,
    "pressure": 2.256409737008921
  },
  "ambient": {
    "temperature": 20.93026691648656,
    "humidity": 25
  },
  "timeCreated": "2020-10-26T15:26:48.0065834Z"
}
``` 

### Verify the data in MongoDB

SSH into your VM and perform some tests:
```bash
ssh azureUser@mongodb-edge-vm.westus2.cloudapp.azure.com

# Show running containers
sudo iotedge list

# Connect to the console of the mongodb container
sudo docker exec -it mongodb /bin/bash

# Open the MongoDB Shell (will default to the local running instance in the container)
mongo

# Show databases, you should see the sample_iot database
show databases

# Show the collections, you should see timeseries
use sample_iot
show collections

# Show example data
db.timeseries.findOne()

# Count the records - will end up in 500 per execution of the temperature sensor container
db.timeseries.count()
```

*Note:* There are well-documented best practices for modelling time series data in MongoDB, please find them documented in a [whitepaper](https://www.mongodb.com/collateral/time-series-best-practices) as well as [code examples](https://github.com/ckurze/mongodb-iot-reference/tree/master/mongodb-timeseries).

