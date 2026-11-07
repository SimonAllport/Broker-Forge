# Phase 0: EntityMetadata payload for the virtual data-source table. POST to EntityDefinitions.
# The primary key ExternalName ("id") cannot be set here; set it afterwards with a PUT.
import json
def label(t): return {"LocalizedLabels":[{"Label":t,"LanguageCode":1033}]}
def mp(v,n): return {"Value":v,"CanBeChanged":False,"ManagedPropertyLogicalName":n}
def s(name, disp, length, primary=False):
    a={"@odata.type":"Microsoft.Dynamics.CRM.StringAttributeMetadata","SchemaName":name,"DisplayName":label(disp),
       "RequiredLevel":{"Value":"None"},"MaxLength":length,"FormatName":{"Value":"Text"}}
    if primary: a["IsPrimaryName"]=True
    return a
print(json.dumps({"@odata.type":"Microsoft.Dynamics.CRM.EntityMetadata","SchemaName":"bf_spikedatasource",
  "DisplayName":label("Spike Data Source"),"DisplayCollectionName":label("Spike Data Sources"),
  "OwnershipType":"OrganizationOwned","HasActivities":False,"HasNotes":False,"IsActivity":False,
  "ChangeTrackingEnabled":False,"CanChangeTrackingBeEnabled":mp(False,"canchangetrackingbeenabled"),
  "CanCreateCharts":mp(False,"cancreatecharts"),"IsAuditEnabled":mp(False,"canmodifyauditsettings"),
  "IsDuplicateDetectionEnabled":mp(False,"canmodifyduplicatedetectionsettings"),
  "ExternalName":"spikedatasource","ExternalCollectionName":"spikedatasources","DataProviderId":"b2112a7e-b26c-42f7-9b63-9a809a9d716f",
  "Attributes":[dict(s("bf_name","Name",100,True), ExternalName="name")]}))
