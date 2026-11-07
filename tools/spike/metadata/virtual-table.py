# Phase 0: EntityMetadata payload for the virtual table. Usage: python3 virtual-table.py <entitydataproviderid> <entitydatasourceid>
import json,sys
dp,ds=sys.argv[1],sys.argv[2]
def label(t): return {"LocalizedLabels":[{"Label":t,"LanguageCode":1033}]}
print(json.dumps({"@odata.type":"Microsoft.Dynamics.CRM.EntityMetadata","SchemaName":"bf_spikerow",
  "DisplayName":label("Spike Row"),"DisplayCollectionName":label("Spike Rows"),
  "OwnershipType":"OrganizationOwned","HasActivities":False,"HasNotes":False,"IsActivity":False,"ChangeTrackingEnabled":False,"CanCreateCharts":{"Value":False,"CanBeChanged":False,"ManagedPropertyLogicalName":"cancreatecharts"},"IsAuditEnabled":{"Value":False,"CanBeChanged":False,"ManagedPropertyLogicalName":"canmodifyauditsettings"},"IsDuplicateDetectionEnabled":{"Value":False,"CanBeChanged":False,"ManagedPropertyLogicalName":"canmodifyduplicatedetectionsettings"},"CanChangeTrackingBeEnabled":{"Value":False,"CanBeChanged":False,"ManagedPropertyLogicalName":"canchangetrackingbeenabled"},
  "DataProviderId":dp,"DataSourceId":ds,"ExternalName":"spikerow","ExternalCollectionName":"spikerows",
  "Attributes":[{"@odata.type":"Microsoft.Dynamics.CRM.StringAttributeMetadata","SchemaName":"bf_name","DisplayName":label("Name"),
     "IsPrimaryName":True,"RequiredLevel":{"Value":"None"},"MaxLength":100,"FormatName":{"Value":"Text"},"ExternalName":"name"}]}))
