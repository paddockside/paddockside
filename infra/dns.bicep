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

// ---- Email (Postmark, transactional) -----------------------------------------------------------------

// Return-Path for Postmark: bounces come back through pm-bounces.mail.paddockside.com.au, which aligns SPF for
// DMARC. Added in Azure because Crazy Domains does not allow a CNAME at this name.
resource postmarkReturnPath 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: zone
  name: 'pm-bounces.mail'
  properties: {
    TTL: 300
    CNAMERecord: { cname: 'pm.mtasv.net' }
  }
}

// DKIM for Postmark sending from mail.paddockside.com.au (the selector and public key come from Postmark).
resource postmarkDkim 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  parent: zone
  name: '20261005053422pm._domainkey.mail'
  properties: {
    TTL: 3600
    TXTRecords: [
      { value: ['k=rsa;p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCDcbcgoklEJtSij3j7wlS913GY31bRwFlkKKXB/XlYKB5/yia3eDXrCnMsvJCITjEHkc8ooBsztRwyJdMANyb+020k2zBFCDddUVpCG0eGRKQfMDqVN7YmpzbHIvl2rxK0qh7psBIqGxXA6ytMr9k20Ulzo0Fhvcxs7dPUGhVGPwIDAQAB'] }
    ]
  }
}

// The app (staff console and owners' portal) at app.paddockside.com.au, so links in emails point at our own
// domain rather than azurewebsites.net, which spam filters distrust. asuid.app proves to App Service that we own it.
resource appHost 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: zone
  name: 'app'
  properties: {
    TTL: 3600
    CNAMERecord: { cname: 'app-paddockside-dev-cd63cr.azurewebsites.net' }
  }
}

resource appHostVerification 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  parent: zone
  name: 'asuid.app'
  properties: {
    TTL: 3600
    TXTRecords: [
      { value: ['11BFB042B5EC5DA5495C0DF00524CD78662E4692D59728123AFBF1762FFD858D'] }
    ]
  }
}

// Inbound email (messaging-channels.md §3.1) goes to Postmark, whose inbound domain is in.paddockside.com.au.
// `*.in` covers every tenant's subdomain (r-{token}@laureloak.in…, {horse}@laureloak.in…); `in` is the
// catch-all ({tenant}@in.paddockside.com.au). Adding a tenant needs no DNS change.
resource inboundMx 'Microsoft.Network/dnsZones/MX@2018-05-01' = [for name in ['in', '*.in']: {
  parent: zone
  name: name
  properties: {
    TTL: 3600
    MXRecords: [
      { preference: 10, exchange: 'inbound.postmarkapp.com' }
    ]
  }
}]

output nameServers array = zone.properties.nameServers
