# Installing BrokerForge

One run of the installer puts BrokerForge into one Dataverse environment and one Azure resource group. It takes 10 to 15 minutes, and you can run it again: each step looks for what it made before.

This was tested on 7 November 2026 by installing into an empty Developer environment and a new resource group in UK South, then setting up the sample Inventory API through the same admin messages the screens use. The live acceptance checks all passed: paging, filters, create, update, delete, the stock action and the refusal of an undeclared filter.

## What gets installed

**In Azure**, in the resource group you name:

- the BrokerForge host, an ASP.NET Core app on Azure Container Apps, built from this repository in your own container registry;
- a user-assigned managed identity for the host, Key Vault, a storage account (encrypted per-user credentials and the host's key ring), Log Analytics and Application Insights;
- optionally, the sample Orders and Inventory API, to try BrokerForge before connecting a real source.

**In Entra ID**, three app registrations:

| App | What it's for |
| --- | --- |
| BrokerForge host (*resource group*) | The audience of the token the Dataverse plug-in sends to the host |
| BrokerForge plug-in (*resource group*) | The plug-in's identity: a federated credential bound to your environment and the plug-in's signing certificate, so it holds no secret |
| BrokerForge connect (*resource group*) | Users sign in with it to connect per-user sources, and administrators sign in with it to finish a connection's role and actions. Its credential is the host's managed identity, so it holds no secret either |

**In Dataverse**, the managed `BrokerForgeCore` solution: the plug-in and data provider, the `bf_connection` table, the **BrokerForge** app, the `BrokerForge administrator` and `BrokerForge host` roles, and the admin messages. The installer also adds the host's application user, binds the plug-in to its app registration, sets the host's URL in the environment, and gives you the `BrokerForge administrator` role.

## Before you start

You need:

- a Dataverse environment and its URL and ID (Power Platform admin center, the environment, **Details**), where you are System Administrator;
- an Azure subscription where you are Owner, or Contributor and User Access Administrator;
- permission to create app registrations in Entra ID. Granting admin consent for the connect app is optional: without it, each user consents the first time they sign in;
- bash (macOS, Linux, or WSL on Windows), with `az` (Azure CLI 2.60 or later), Python 3.8 or later, `curl`, `openssl` and `git`. Docker isn't needed: the host image is built in Azure.

BrokerForge runs in the Azure public cloud only, because its sign-in addresses are `login.microsoftonline.com`.

## Install

1. Get the code at the release you are installing, and the managed core solution from the same release:

   ```bash
   git clone --branch v0.1.0 https://github.com/SimonAllport/Broker-Forge.git
   cd BrokerForge
   ```

   Download `BrokerForgeCore_managed.zip` from the [release page](https://github.com/SimonAllport/Broker-Forge/releases).

2. Sign in to Azure and pick the subscription:

   ```bash
   az login
   az account set --subscription "<subscription name or ID>"
   ```

3. Copy the settings file and fill it in. It holds no secrets.

   ```bash
   cp tools/install/brokerforge.env.example brokerforge.env
   ```

   | Setting | Meaning |
   | --- | --- |
   | `ORG_URL` | The environment's URL, such as `https://contoso.crm4.dynamics.com` |
   | `ENVIRONMENT_ID` | The environment's ID |
   | `RESOURCE_GROUP` | A new or empty resource group (default `rg-brokerforge`) |
   | `LOCATION` | The Azure region, such as `uksouth`. Some regions don't take new resources for every subscription: if the install stops with "The selected region is currently not accepting new customers", delete the empty resource group and choose another |
   | `PREFIX` | The start of every Azure resource name (default `brokerforge`) |
   | `PACKAGE` | Where you saved `BrokerForgeCore_managed.zip`, relative to the settings file |
   | `SAMPLES` | `true` adds the sample Inventory and Orders API |

4. Run the installer:

   ```bash
   tools/install/install.sh brokerforge.env
   ```

   It ends by printing the host's URL. The IDs it made are kept outside the repository, in `~/.brokerforge/installs/<resource group>`. If Azure refuses a deployment, it tries twice more and then prints Azure's own reason.

## After the install

Open the **BrokerForge** app in the environment. It opens on **Connections**. To add your first source, follow [part 1 of the how-to guide](guide/how-to-guide.md#part-1-add-a-rest-connection-administrators).

Give people roles as they need them:

- `BrokerForge administrator` to whoever sets up connections;
- for each source, the role Generate makes, `<Name> user`, to whoever uses its table.

### Try the sample API

With `SAMPLES=true`, the installer prints the sample API's base URL. Add a connection named `InventoryAPI` with that URL, authentication **Static, API key in the query** (parameter `api_key`), and paste the definition from [tools/mock-api/inventory-openapi.yaml](../tools/mock-api/inventory-openapi.yaml). Then follow the guide from step 2. When the guide asks for the key, read it from your Key Vault (the vault name is in the installer's output folder, `dev-ids.env`) and paste it into the host's key page:

```bash
az keyvault secret show --vault-name <vault> -n inventory-api-key --query value -o tsv
```

## Updating

Check out the new release's tag, download its `BrokerForgeCore_managed.zip`, and run the installer again with the same settings file. It builds the new host image, deploys it and imports the new core over the old one.

## Removing BrokerForge

1. In Dataverse, delete each source's solution (`BrokerForge<Name>`), then the `BrokerForgeCore` solution.
2. Delete the resource group: `az group delete -n <resource group>`. Key Vault keeps a soft-deleted copy for 90 days; purge it if you want the name back.
3. Delete the three app registrations named after the resource group.

## Limits of this release

- Update BrokerForge by running the installer again, not by uninstalling and reinstalling it. In testing, three uninstall and reinstall cycles in one evening left Dataverse refusing the connection screens' Read and Generate with "Entity 'solution' … Does Not Exist" until the environment recovers.

- The plug-in in the core solution is signed with a self-signed certificate that expires on 31 October 2027. The plug-in app's federated credential names that certificate's hash, so a release signed with a new certificate needs the installer run again.
- The host runs one replica at all times (a cold start took 23.5 seconds in testing), so it costs money even when idle.
- One installer run serves one environment. The host can serve several, but the installer doesn't yet add a second environment to an existing host.
