// The public DNS zone for paddockside.com.au, copied from the records at Crazy Domains on 6 Oct 2026.
// Creating it changes nothing for the public until the domain's nameservers are switched to the Azure ones
// (at Crazy Domains); until then Crazy Domains stays authoritative.
//
//   az deployment group create --resource-group rg-paddockside-dev --template-file infra/dns.bicep

param zoneName string = 'paddockside.com.au'

var tags = {
  product: 'paddockside'
  managedBy: 'bicep'
}

resource zone 'Microsoft.Network/dnsZones@2018-05-01' = {
  name: zoneName
  location: 'global'
  tags: tags
  properties: {
    zoneType: 'Public'
  }
}

// ---- Website -----------------------------------------------------------------------------------------

resource apexA 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: zone
  name: '@'
  properties: {
    TTL: 300
    ARecords: [{ ipv4Address: '103.67.235.120' }]
  }
}

resource wwwA 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: zone
  name: 'www'
  properties: {
    TTL: 300
    ARecords: [{ ipv4Address: '103.67.235.120' }]
  }
}

// ---- Email (Titan) -----------------------------------------------------------------------------------

resource apexMx 'Microsoft.Network/dnsZones/MX@2018-05-01' = {
  parent: zone
  name: '@'
  properties: {
    TTL: 300
    MXRecords: [
      { preference: 10, exchange: 'mx1.titan.email' }
      { preference: 20, exchange: 'mx2.titan.email' }
      // Microsoft's domain-verification MX: never receives mail. Safe to remove once the domain stays verified.
      { preference: 50, exchange: 'ms22572786.msv1.invalid' }
    ]
  }
}

resource apexTxt 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  parent: zone
  name: '@'
  properties: {
    TTL: 300
    TXTRecords: [
      // Microsoft Entra domain verification for the paddockside.com.au custom domain.
      { value: ['MS=ms22572786'] }
      { value: ['v=spf1 include:spf.titan.email ~all'] }
    ]
  }
}

resource titanDkim 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  parent: zone
  name: 'titan1._domainkey'
  properties: {
    TTL: 300
    TXTRecords: [
      { value: ['v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCEABgc7G8/52DxUtcXw3iObupOF1zyn3hB887Y/jwT5/RlallkXqWPOHJ2fxtzzsci/3UYBO3DQgOmvR+7c3q+J99UAXbJVuB5xkvSOgmh/G3ZoJPvSDPIrB7xjvN9ahefxrHxc+Lavex4a7kUzA5TpQ2l5oSha8+MJO+BsNo4AQIDAQAB'] }
    ]
  }
}

output nameServers array = zone.properties.nameServers
