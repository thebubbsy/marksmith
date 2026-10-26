#!/usr/bin/env python3
"""
M365 / Entra ID Deep Tenant Recon & Self-Healing Anti-AiTM Canary TUI

Capabilities:
1. Deep Unauthenticated M365 / Entra ID OSINT:
   - OpenID Connect discovery (Tenant GUID, Region Scope/Sub-Scope, Cloud Instance)
   - GetUserRealm & GetCredentialType (Federation IdP URLs, Seamless SSO / DesktopSsoEnabled,
     FIDO2/Passkey support, Windows Hello NGC, User Enumeration posture)
   - Autodiscover SOAP GetFederationInformation (Enumerates ALL sister/co-tenant domains
     registered inside the same M365 tenancy + .onmicrosoft.com root slug)
   - DKIM CNAME Tenant Slug Fallback (selector1/selector2._domainkey.<domain>)
   - SharePoint Online & OneDrive Instance Probing (<tenant>.sharepoint.com build & health score)
   - Microsoft Defender for Identity (MDI / Azure ATP) Sensor API detection (<tenant>sensorapi.atp.azure.com)
   - Exchange Autodiscover V2 JSON (Exchange Online vs. Hybrid On-Prem server leak)
   - Teams External Federation (_sipfederationtls._tcp SRV) & Intune MDM Enrollment detection
   - DNS-over-HTTPS (DoH) Mail Gateway, DMARC, Trusted SPF Partners & Verified SaaS TXT Fingerprinting
2. Anti-AiTM & Canary Token Detection + Active/Recurring Self-Healing Triggering:
   - SecuranceCyber/aitm-detect (?whr= HTML scraping) + GetCredentialType CustomCSSUrl
   - Static 6-Vector Trigger Matrix & CSS Selector/DOM Trap Analyzer
   - Zero-nag Active Trigger Mode [7] & Self-Healing Recurring Scheduled Task [9]
   - Scheduled task worker auto-detects rotated/changed CSS Canary tokens on each run
     and self-heals m365_canary_recurring.json for all future runs.
"""

import argparse
from contextlib import nullcontext
import ipaddress
import json
import logging
import platform
import random
import re
import shutil
import socket
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional
from urllib.parse import quote, urlparse, urlunparse

import requests
from requests.exceptions import RequestException, Timeout

try:
    from rich import box
    from rich.console import Console
    from rich.panel import Panel
    from rich.prompt import Confirm, Prompt
    from rich.rule import Rule
    from rich.syntax import Syntax
    from rich.table import Table
except ImportError:
    print("[-] Missing 'rich' dependency. Run with the .venv Python interpreter.")
    sys.exit(1)

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

logger = logging.getLogger(__name__)
_term_console = Console()

# ---------------------------------------------------------------------------
# GUI mode: capture rich output as HTML instead of printing to terminal
# ---------------------------------------------------------------------------
_GUI_MODE = False


class HtmlConsole:
    """Drop-in replacement for rich.console.Console that records HTML."""

    def __init__(self):
        self._console = Console(record=True, width=155, force_terminal=False, color_system="truecolor")
        self._html_cache: list = []

    def print(self, *args, **kwargs):
        self._console.print(*args, **kwargs)

    def status(self, *args, **kwargs):
        return nullcontext()

    def export_html(self) -> str:
        return self._console.export_html(inline_styles=True)

    def clear(self):
        self._console = Console(record=True, width=155, force_terminal=False, color_system="truecolor")


_gui_console: Optional[HtmlConsole] = None


def _get_console():
    """Return the active console — HtmlConsole in GUI mode, rich Console otherwise."""
    return (_GUI_MODE and _gui_console) or _term_console


class _ConsoleRouter:
    """Routes console.print() and console.status() to _get_console() automatically."""

    def print(self, *args, **kwargs):
        return _get_console().print(*args, **kwargs)

    def status(self, *args, **kwargs):
        return _get_console().status(*args, **kwargs)

    def __getattr__(self, name: str):
        return getattr(_get_console(), name)


console = _ConsoleRouter()

REQUEST_TIMEOUT = 15
BASE_URL = "https://login.microsoftonline.com"
DOH_URL = "https://cloudflare-dns.com/dns-query"

CSS_URL_PATTERN = r'customCssUrl":"(https://[^"]+)'
IMAGE_URL_PATTERN = (
    r'url\([\'"]?(https?://[^\'")\s]+|//[^\'")\s]+|data:image[^\'")\s]+|/[^\'")\s]+)[\'"]?\)'
)
IMPORT_URL_PATTERN = r'@import\s+(?:url\()?[\'"]?(https?://[^\'")\s]+|//[^\'")\s]+)[\'"]?\)?'
DOMAIN_PATTERN = (
    r"^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?"
    r"(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$"
)

CANARY_PROVIDER_PATTERNS = [
    (r"dakg4cmpuclai\.cloudfront\.net", "Thinkst Canarytokens (Free Tier CloudFront)"),
    (r"[a-z0-9]+\.cloudfront\.net/.+/img\.gif", "Thinkst Canarytokens (CloudFront GIF Beacon)"),
    (r"canarytokens\.(com|org|net)", "Thinkst Canarytokens"),
    (r"o3n\.io", "Thinkst Canarytokens (o3n.io Shortener)"),
    (r"thinkst\.com", "Thinkst Canary (Enterprise / Hosted)"),
    (r"interact\.sh|oast\.(pro|live|site|online|fun|me)", "ProjectDiscovery Interactsh (OAST)"),
]

MICROSOFT_LEGIT_HOSTS = (
    "microsoft.com",
    "microsoftonline.com",
    "msauth.net",
    "msauthimages.net",
    "msftauth.net",
    "msftauthimages.net",
    "azure.com",
    "azureedge.net",
    "office.com",
    "office365.com",
    "live.com",
    "gfx.ms",
)

SAAS_TXT_FINGERPRINTS = [
    (r"MS=ms\d+", "Microsoft 365 Domain Verification"),
    (r"v=verifydomain MS=", "Microsoft 365 Verification"),
    (r"google-site-verification=", "Google Workspace / Search Console"),
    (r"atlassian-domain-verification=", "Atlassian Cloud (Jira / Confluence)"),
    (r"knowbe4-site-verification=", "KnowBe4 Security Awareness / PhishER"),
    (r"docusign=", "DocuSign Enterprise"),
    (r"slack-domain-verification=", "Slack Enterprise Grid"),
    (r"zoom-domain-verification=", "Zoom Enterprise"),
    (r"apple-domain-verification=", "Apple Business Manager (ABM)"),
    (r"cisco-ci-domain-verification=", "Cisco Webex / Umbrella"),
    (r"miro-verification=", "Miro Enterprise"),
    (r"canva-site-verification=", "Canva Enterprise"),
    (r"postman-domain-verification=", "Postman Enterprise"),
    (r"openai-domain-verification=", "OpenAI Enterprise / ChatGPT Team"),
    (r"anthropic-domain-verification=", "Anthropic Claude Enterprise"),
    (r"adobe-idp-site-verification=", "Adobe Federated ID (SSO)"),
    (r"facebook-domain-verification=", "Meta Business Suite"),
    (r"hubspot-developer-verification=", "HubSpot CRM"),
    (r"stripe-verification=", "Stripe Payments"),
    (r"zendesk-verification=", "Zendesk Support"),
    (r"have-i-been-pwned-verification=", "Have I Been Pwned (Domain Monitor)"),
    (r"wiz-domain-verification=", "Wiz Cloud Security"),
    (r"snyk-domain-verification=", "Snyk Security"),
    (r"jamf-site-verification=", "Jamf Pro (macOS/iOS MDM)"),
    (r"teamviewer-sso-verification=", "TeamViewer Tensor SSO"),
]

MAIL_GATEWAY_FINGERPRINTS = [
    (r"mail\.protection\.outlook\.com", "Microsoft Defender / Exchange Online Protection (EOP)"),
    (r"pphosted\.com|proofpoint\.com", "Proofpoint Email Protection"),
    (r"mimecast\.com", "Mimecast Secure Email Gateway"),
    (r"barracudanetworks\.com|ess\.barracuda\.com", "Barracuda Email Security Gateway"),
    (r"iphmx\.com|ironport", "Cisco Secure Email (IronPort)"),
    (r"messagelabs\.com|symantec", "Broadcom / Symantec MessageLabs"),
    (r"Sophos|reflexion\.net", "Sophos Email Security"),
    (r"trendmicro\.com", "Trend Micro Email Security"),
    (r"hornetsecurity\.com", "Hornetsecurity"),
    (r"forcepoint\.com|mailcontrol\.com", "Forcepoint Email Security"),
    (r"fireeyecloud\.com|trellix", "Trellix / FireEye Email Security"),
    (r"google\.com|googlemail\.com", "Google Workspace Gmail Routing"),
]

HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/124.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/json,text/plain,*/*",
}


@dataclass
class ScanResult:
    target_input: str
    domain: str
    probe_user: str
    valid_domain: bool = True
    tenant_found: bool = False
    tenant_id: Optional[str] = None
    tenant_region: Optional[str] = None
    tenant_sub_region: Optional[str] = None
    cloud_instance: Optional[str] = None
    issuer: Optional[str] = None
    login_page: Optional[str] = None
    whr_login_url: Optional[str] = None
    auth_endpoint: Optional[str] = None
    namespace_type: Optional[str] = None
    federation_brand: Optional[str] = None
    federation_url: Optional[str] = None
    federation_version: Optional[str] = None
    desktop_sso_enabled: Optional[bool] = None
    fido_supported: Optional[bool] = None
    remote_ngc_supported: Optional[bool] = None
    has_password: Optional[bool] = None
    user_enum_status: Optional[str] = None
    tenant_slug: Optional[str] = None
    onmicrosoft_domain: Optional[str] = None
    mail_onmicrosoft_domain: Optional[str] = None
    co_tenant_domains: List[str] = field(default_factory=list)
    federation_token_issuers: List[str] = field(default_factory=list)
    autodiscover_endpoint: Optional[str] = None
    exchange_mode: Optional[str] = None
    sharepoint_url: Optional[str] = None
    sharepoint_status: Optional[str] = None
    sharepoint_version: Optional[str] = None
    sharepoint_health_score: Optional[str] = None
    onedrive_url: Optional[str] = None
    onedrive_status: Optional[str] = None
    teams_federation: Optional[str] = None
    lync_pool: Optional[str] = None
    intune_mdm: Optional[str] = None
    mdi_sensor_detected: Optional[bool] = None
    mdi_endpoint: Optional[str] = None
    mx_records: List[str] = field(default_factory=list)
    mail_gateways: List[str] = field(default_factory=list)
    spf_record: Optional[str] = None
    spf_trusted_domains: List[str] = field(default_factory=list)
    dmarc_policy: Optional[str] = None
    dkim_cnames: List[str] = field(default_factory=list)
    verified_saas: List[str] = field(default_factory=list)
    raw_txt_records: List[str] = field(default_factory=list)
    css_url: Optional[str] = None
    css_discovery_source: Optional[str] = None
    banner_logo_url: Optional[str] = None
    illustration_url: Optional[str] = None
    tile_logo_url: Optional[str] = None
    boiler_plate_text: Optional[str] = None
    keep_me_signed_in: Optional[bool] = None
    css_content: Optional[str] = None
    has_canary: bool = False
    needs_manual_review: bool = False
    canary_provider: Optional[str] = None
    canary_urls: List[str] = field(default_factory=list)
    all_extracted_urls: List[str] = field(default_factory=list)
    raw_cred_response: dict = field(default_factory=dict)
    error: Optional[str] = None


@dataclass
class TriggerAttempt:
    vector_name: str
    url_requested: str
    method: str
    request_headers: dict
    status_code: Optional[int] = None
    elapsed_ms: Optional[float] = None
    error: Optional[str] = None
    response_snippet: Optional[str] = None


@dataclass
class TenantRecon:
    domain: str
    tenant_id: Optional[str] = None
    tenant_name: Optional[str] = None
    brand_name: Optional[str] = None
    region: Optional[str] = None
    cloud_instance: Optional[str] = None
    namespace_type: Optional[str] = None
    federation_brand: Optional[str] = None
    seamless_sso: Optional[bool] = None
    desktop_sso: Optional[bool] = None
    verified_domains: List[dict] = field(default_factory=list)
    adfs_server: Optional[str] = None
    auth_endpoints: dict = field(default_factory=dict)
    error: Optional[str] = None


@dataclass
class ReconProfile:
    name: str = "default"
    domain: str = ""
    # External review probe toggles
    enable_openid: bool = True
    enable_getuserrealm: bool = True
    enable_seamless_sso: bool = True
    enable_user_onedrive: bool = True
    enable_user_getcredentialtype: bool = True
    enable_autodiscover: bool = True
    enable_ews: bool = True
    enable_sharepoint: bool = True
    enable_azure_services: bool = True
    enable_lync: bool = True
    enable_dynamics: bool = True
    # Internal posture review probe toggles
    enable_internal_nmap: bool = True
    enable_internal_dns: bool = True
    enable_internal_directory: bool = True
    enable_internal_spn: bool = True
    enable_internal_preauth: bool = True
    enable_internal_shares: bool = True
    enable_internal_cpassword: bool = True
    enable_internal_cert_templates: bool = True
    enable_internal_sessions: bool = True
    enable_internal_bloodhound: bool = True
    # Rate limiting & execution limits
    inter_request_delay_ms: int = 300
    max_users_to_enum: int = 50
    # Output mode & formatting: verbose, json, csv, table
    output_mode: str = "verbose"
    include_raw_responses: bool = False
    # Legacy knock mode compatibility
    knock_mode: bool = False
    tool_signature_ua: str = "TeamFiltration/3.5.0"
    include_signin_knock: bool = False
    include_onedrive_knock: bool = False
    include_graph_knock: bool = False
    burst_count: int = 25


DETECTION_RISK = {
    "recon_openid": {"risk": "Very Low", "audit_logs": "No", "notes": "Public OIDC discovery API"},
    "recon_getuserrealm": {"risk": "Very Low", "audit_logs": "No", "notes": "Public Federation/Realm endpoint"},
    "recon_seamless_sso": {"risk": "None", "audit_logs": "No", "notes": "Silent WHR parameter configuration parse"},
    "enum_user_onedrive": {"risk": "None", "audit_logs": "No (Default) / SPO UAL", "notes": "Silent personal-site presence check; 401/403 vs 404"},
    "enum_user_getcredentialtype": {"risk": "Medium", "audit_logs": "Possible", "notes": "Tenant auth endpoint query; rate-limited & logged under SigninLogs"},
    "probe_autodiscover": {"risk": "Low", "audit_logs": "Minimal", "notes": "Autodiscover v2 JSON service discovery"},
    "probe_ews": {"risk": "Low", "audit_logs": "Minimal", "notes": "Exchange Web Services endpoint check"},
    "probe_sharepoint": {"risk": "Low", "audit_logs": "Minimal", "notes": "SharePoint & OneDrive tenant root headers"},
    "probe_azure_services": {"risk": "Very Low", "audit_logs": "No", "notes": "Passive DNS resolution for Azure cloud PaaS services"},
    "probe_lync": {"risk": "Low", "audit_logs": "Minimal", "notes": "SIP / Skype for Business autodiscover endpoint"},
    "probe_dynamics": {"risk": "Very Low", "audit_logs": "No", "notes": "Passive DNS resolution for Dynamics CRM clusters"},
    "run_roadrecon": {"risk": "Medium", "audit_logs": "Yes", "notes": "Authenticated directory read queries logged to Microsoft Graph Activity Logs"},
    "run_azurehound": {"risk": "Medium", "audit_logs": "Yes", "notes": "Authenticated Entra ID structure inventory in Graph / MS Graph logs"},
    "internal_nmap_discover": {"risk": "Medium", "audit_logs": "Network/EDR", "notes": "Network port scan; logged in network sensors and host firewalls"},
    "internal_dns_enum": {"risk": "Low", "audit_logs": "DNS Server", "notes": "Standard DNS queries and zone transfer attempt logged to DNS audit logs"},
    "internal_directory_enum": {"risk": "Low", "audit_logs": "Domain Controller (Event 4662)", "notes": "Standard LDAP directory queries; low baseline anomaly"},
    "internal_spn_inventory": {"risk": "Low", "audit_logs": "Domain Controller (Event 4662)", "notes": "Read-only LDAP query for ServicePrincipalName attribute; no ticket request"},
    "internal_preauth_inventory": {"risk": "Low", "audit_logs": "Domain Controller (Event 4662)", "notes": "Read-only LDAP query for DONT_REQ_PREAUTH flag; no AS-REQ ticket request"},
    "internal_share_inventory": {"risk": "Low", "audit_logs": "File Server (Event 5140)", "notes": "Read-only SMB share enumeration via NetShareEnum / smbclient"},
    "internal_sysvol_cpassword_scan": {"risk": "Low", "audit_logs": "File Server (Event 5145)", "notes": "Standard file read in SYSVOL; reports filenames containing cpassword; does not decrypt"},
    "internal_certificate_template_audit": {"risk": "Low", "audit_logs": "Domain Controller / CA", "notes": "Read-only LDAP query of Public Key Services container for template flags"},
    "internal_session_inventory": {"risk": "Medium", "audit_logs": "Host Event Logs", "notes": "Read-only query of active sessions via quser/qwinsta or RPC"},
    "internal_path_mapping": {"risk": "Medium", "audit_logs": "Domain Controller", "notes": "Read-only LDAP / RPC relationship collection via BloodHound collector"},
}

DETECTION_RISK_MATRIX = [
    {"method": "recon_openid", "risk": "Very Low", "audit_logs": "No", "notes": "Public OIDC discovery API"},
    {"method": "recon_getuserrealm", "risk": "Very Low", "audit_logs": "No", "notes": "Public Federation/Realm endpoint"},
    {"method": "recon_seamless_sso", "risk": "None", "audit_logs": "No", "notes": "Silent WHR parameter configuration parse"},
    {"method": "enum_user_onedrive", "risk": "None", "audit_logs": "No (Default) / SPO UAL", "notes": "Silent personal-site presence check; 401/403 vs 404"},
    {"method": "enum_user_getcredentialtype", "risk": "Medium", "audit_logs": "Possible", "notes": "Tenant auth endpoint query; rate-limited & logged under SigninLogs"},
    {"method": "probe_autodiscover", "risk": "Low", "audit_logs": "Minimal", "notes": "Autodiscover v2 JSON service discovery"},
    {"method": "probe_ews", "risk": "Low", "audit_logs": "Minimal", "notes": "Exchange Web Services endpoint check"},
    {"method": "probe_sharepoint", "risk": "Low", "audit_logs": "Minimal", "notes": "SharePoint & OneDrive tenant root headers"},
    {"method": "probe_azure_services", "risk": "Very Low", "audit_logs": "No", "notes": "Passive DNS resolution for Azure cloud PaaS services"},
    {"method": "probe_lync", "risk": "Low", "audit_logs": "Minimal", "notes": "SIP / Skype for Business autodiscover endpoint"},
    {"method": "probe_dynamics", "risk": "Very Low", "audit_logs": "No", "notes": "Passive DNS resolution for Dynamics CRM clusters"},
    {"method": "run_roadrecon", "risk": "Medium", "audit_logs": "Yes", "notes": "Authenticated directory read queries logged to Microsoft Graph Activity Logs"},
    {"method": "run_azurehound", "risk": "Medium", "audit_logs": "Yes", "notes": "Authenticated Entra ID structure inventory in Graph / MS Graph logs"},
    {"method": "internal_nmap_discover", "risk": "Medium", "audit_logs": "Network/EDR", "notes": "Network port scan; logged in network sensors and host firewalls"},
    {"method": "internal_dns_enum", "risk": "Low", "audit_logs": "DNS Server", "notes": "Standard DNS queries and zone transfer attempt logged to DNS audit logs"},
    {"method": "internal_directory_enum", "risk": "Low", "audit_logs": "Domain Controller (Event 4662)", "notes": "Standard LDAP directory queries; low baseline anomaly"},
    {"method": "internal_spn_inventory", "risk": "Low", "audit_logs": "Domain Controller (Event 4662)", "notes": "Read-only LDAP query for ServicePrincipalName attribute; no ticket request"},
    {"method": "internal_preauth_inventory", "risk": "Low", "audit_logs": "Domain Controller (Event 4662)", "notes": "Read-only LDAP query for DONT_REQ_PREAUTH flag; no AS-REQ ticket request"},
    {"method": "internal_share_inventory", "risk": "Low", "audit_logs": "File Server (Event 5140)", "notes": "Read-only SMB share enumeration via NetShareEnum / smbclient"},
    {"method": "internal_sysvol_cpassword_scan", "risk": "Low", "audit_logs": "File Server (Event 5145)", "notes": "Standard file read in SYSVOL; reports filenames containing cpassword; does not decrypt"},
    {"method": "internal_certificate_template_audit", "risk": "Low", "audit_logs": "Domain Controller / CA", "notes": "Read-only LDAP query of Public Key Services container for template flags"},
    {"method": "internal_session_inventory", "risk": "Medium", "audit_logs": "Host Event Logs", "notes": "Read-only query of active sessions via quser/qwinsta or RPC"},
    {"method": "internal_path_mapping", "risk": "Medium", "audit_logs": "Domain Controller", "notes": "Read-only LDAP / RPC relationship collection via BloodHound collector"},
]

def annotate_with_risk(probe: str, result: dict) -> dict:
    """Annotate a probe result dict with its detection risk reference."""
    meta = DETECTION_RISK.get(probe, {"risk": "Low", "audit_logs": "Minimal", "notes": "Standard assessment probe"})
    if isinstance(result, dict):
        result["_detection_risk"] = meta
    elif isinstance(result, list):
        for item in result:
            if isinstance(item, dict):
                item["_detection_risk"] = meta
    return result

# ---------------------------------------------------------------------------
# Profile Management System (~/.m365recon/profiles/)
# ---------------------------------------------------------------------------
PROFILES_DIR = Path.home() / ".m365recon" / "profiles"
LAST_PROFILE_FILE = Path.home() / ".m365recon" / "last_profile.json"
_CURRENT_PROFILE: ReconProfile = ReconProfile()
_SESSION_ENGAGEMENT_ID: Optional[str] = None

def get_session_engagement_id() -> str:
    global _SESSION_ENGAGEMENT_ID
    if not _SESSION_ENGAGEMENT_ID:
        eid = Prompt.ask("[bold green]Enter Engagement Identifier (e.g. ENG-2026-01)[/bold green]", default="ENG-DEFAULT").strip()
        _SESSION_ENGAGEMENT_ID = re.sub(r"[^a-zA-Z0-9_-]", "_", eid) or "ENG-DEFAULT"
    return _SESSION_ENGAGEMENT_ID

def log_session_activity(target: str, command_description: str):
    """Record assessment activity to ~/.m365recon/session_<engagement_id>.log"""
    try:
        log_dir = Path.home() / ".m365recon"
        log_dir.mkdir(parents=True, exist_ok=True)
        eid = get_session_engagement_id()
        log_path = log_dir / f"session_{eid}.log"
        ts = datetime.now(timezone.utc).isoformat()
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(f"[{ts}] TARGET: {target} | ACTION: {command_description}\n")
    except Exception as exc:
        logger.debug(f"Failed to write session log: {exc}")

def save_profile(profile: ReconProfile) -> Path:
    PROFILES_DIR.mkdir(parents=True, exist_ok=True)
    p_file = PROFILES_DIR / f"{profile.name}.json"
    p_file.write_text(json.dumps(profile.__dict__, indent=2), encoding="utf-8")
    LAST_PROFILE_FILE.parent.mkdir(parents=True, exist_ok=True)
    LAST_PROFILE_FILE.write_text(json.dumps({"last_used": profile.name}), encoding="utf-8")
    return p_file

def load_profile(name: str) -> Optional[ReconProfile]:
    p_file = PROFILES_DIR / f"{name}.json"
    if p_file.exists():
        try:
            data = json.loads(p_file.read_text(encoding="utf-8"))
            prof = ReconProfile(**{k: v for k, v in data.items() if k in ReconProfile.__dataclass_fields__})
            prof.name = name
            LAST_PROFILE_FILE.parent.mkdir(parents=True, exist_ok=True)
            LAST_PROFILE_FILE.write_text(json.dumps({"last_used": name}), encoding="utf-8")
            return prof
        except Exception as exc:
            logger.debug(f"Error loading profile {name}: {exc}")
    return None

def load_last_profile() -> ReconProfile:
    if LAST_PROFILE_FILE.exists():
        try:
            info = json.loads(LAST_PROFILE_FILE.read_text(encoding="utf-8"))
            name = info.get("last_used")
            if name:
                loaded = load_profile(name)
                if loaded:
                    return loaded
        except Exception:
            pass
    return ReconProfile()

def list_profiles() -> List[str]:
    if not PROFILES_DIR.exists():
        return []
    return sorted([f.stem for f in PROFILES_DIR.glob("*.json")])

def delete_profile(name: str) -> bool:
    p_file = PROFILES_DIR / f"{name}.json"
    if p_file.exists():
        p_file.unlink()
        return True
    return False

# Initialize profile on startup
_CURRENT_PROFILE = load_last_profile()


# Shared state for GUI mode
_last_result: Optional[ScanResult] = None
_history: List[ScanResult] = []
_last_trigger_context: Optional[dict] = None
_trigger_history: List[TriggerAttempt] = []


TRIGGER_VECTOR_PRESETS = {
    "1": {
        "name": "1. Foreign Referer Origin",
        "description": "Referer: https://evil-phish-site.example/login + browser Sec-Fetch headers.",
        "headers": {
            "Referer": "https://evil-phish-site.example/login",
            "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            "Sec-Fetch-Dest": "image",
            "Sec-Fetch-Mode": "no-cors",
            "Sec-Fetch-Site": "cross-site",
        },
        "use_http": False,
    },
    "2": {
        "name": "2. Missing / Stripped Referer",
        "description": "Send the request with NO Referer header at all.",
        "headers": {
            "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            "Sec-Fetch-Dest": "image",
            "Sec-Fetch-Mode": "no-cors",
            "Sec-Fetch-Site": "none",
        },
        "remove_referer": True,
        "use_http": False,
    },
    "3": {
        "name": "3. Scheme Downgrade (HTTP)",
        "description": "Forces http:// on the beacon URL + HTTP Referer.",
        "headers": {"Referer": "http://evil-phish-site.example/login"},
        "use_http": True,
    },
    "4": {
        "name": "4. CLI / Scraper User-Agent",
        "description": "User-Agent: python-requests/2.31.0 with non-Microsoft Referer.",
        "headers": {
            "User-Agent": "python-requests/2.31.0",
            "Referer": "https://evil-phish-site.example/login",
        },
        "strip_sec_fetch": True,
        "use_http": False,
    },
    "5": {
        "name": "5. Headless / Missing Sec-Fetch-*",
        "description": "HeadlessChrome UA, no Sec-Fetch-* headers.",
        "headers": {
            "User-Agent": (
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
                "(KHTML, like Gecko) HeadlessChrome/124.0.0.0 Safari/537.36"
            ),
            "Referer": "https://evil-phish-site.example/login",
        },
        "strip_sec_fetch": True,
        "use_http": False,
    },
    "6": {
        "name": "6. Trap DOM Selector Fetch",
        "description": "Legitimate MS Referer (trap fires on any fetch of an unrendered selector URL).",
        "headers": {
            "Referer": "https://login.microsoftonline.com/",
            "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            "Sec-Fetch-Dest": "image",
            "Sec-Fetch-Mode": "no-cors",
            "Sec-Fetch-Site": "cross-site",
        },
        "use_http": False,
    },
    "7": {
        "name": "7. Tool-Signature UA (SIEM Knock)",
        "description": "UA string matching known recon tool signatures (TeamFiltration/3.5.0) — trips SIEM rules directly.",
        "headers": {
            "User-Agent": "TeamFiltration/3.5.0",
            "Referer": "https://evil-phish-site.example/",
        },
        "strip_sec_fetch": True,
        "use_http": False,
    },
}

POWERSHELL_WORKER_TEMPLATE = r"""
<#
    M365 Canary Self-Healing Recurring Trigger Worker
    Auto-generated by M365 Canary TUI (menu [9]).
    - On each tick, probes the target domain's live M365 Custom CSS (?whr= & GetCredentialType)
      to detect if the tenant rotated or changed their Canary token URL(s).
    - If a token change is detected, automatically self-heals $StatePath with a fresh
      randomized URL pool so all future scheduled task ticks target the new token.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [string]$StatePath = "$PSScriptRoot\m365_canary_recurring.json",
    [Parameter(Mandatory=$false)]
    [string]$LogPath   = "$PSScriptRoot\m365_canary_recurring.log"
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

function Write-Log {
    param([string]$Message)
    $ts = (Get-Date).ToUniversalTime().ToString("o")
    "$ts $Message" | Out-File -FilePath $LogPath -Append -Encoding utf8
}

function Get-RandomizedUrlPool {
    param(
        [string[]]$BaseUrls,
        [bool]$Randomize = $true,
        [int]$PoolSize = 20
    )
    if (-not $Randomize) {
        return ,$BaseUrls
    }
    $chars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()
    $pool = New-Object System.Collections.Generic.List[string]
    $perUrl = [Math]::Max(1, [int][Math]::Floor($PoolSize / [Math]::Max(1, $BaseUrls.Count)))
    foreach ($u in $BaseUrls) {
        for ($i = 0; $i -lt $perUrl; $i++) {
            $nonce = -join (1..10 | ForEach-Object { $chars | Get-Random })
            $target = $u
            if ((Get-Random -Minimum 0.0 -Maximum 1.0) -lt 0.15 -and $target -match '^https://') {
                $target = 'http://' + $target.Substring(8)
            }
            if ($target.Contains('?')) {
                $target = "${target}&cb=${nonce}"
            } else {
                $target = "${target}?cb=${nonce}"
            }
            $pool.Add($target)
        }
    }
    return ,($pool | Sort-Object { Get-Random })
}

function Get-LiveTenantCanaryUrls {
    param(
        [string]$Domain,
        [string]$FallbackCssUrl = $null,
        [bool]$BypassCache = $false
    )
    if ((-not $Domain -or $Domain -eq "custom-target" -or $Domain -eq "custom-url") -and (-not $FallbackCssUrl)) {
        return $null
    }
    $ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
    $cssUrl = $null

    if ($Domain -and $Domain -ne "custom-target" -and $Domain -ne "custom-url") {
        # 1. Try ?whr=<domain> HTML $Config extraction (SecuranceCyber/aitm-detect method)
        try {
            $whrUri = "https://login.microsoftonline.com/?whr=$Domain"
            if ($BypassCache) { $whrUri += "&_ts=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" }
            $whrResp = Invoke-WebRequest -Uri $whrUri `
                                         -Headers @{ "User-Agent" = $ua; "Cache-Control" = "no-cache" } `
                                         -UseBasicParsing -TimeoutSec 15
            if ($whrResp.Content -match 'customCssUrl":"(https://[^"]+)') {
                $cssUrl = [System.Text.RegularExpressions.Regex]::Unescape($Matches[1])
            }
        } catch {}

        # 2. Fallback to GetCredentialType JSON
        if (-not $cssUrl) {
            try {
                $body = @{
                    username = "canarycheck@$Domain"
                    isOtherIdpSupported = $true
                    checkPhones = $false
                    isRemoteNGCSupported = $true
                    isCookieBannerShown = $false
                    isFidoSupported = $true
                    originalRequest = ""
                } | ConvertTo-Json
                $credResp = Invoke-RestMethod -Uri "https://login.microsoftonline.com/common/GetCredentialType" `
                                              -Method POST -Body $body -ContentType "application/json" `
                                              -Headers @{ "User-Agent" = $ua; "Cache-Control" = "no-cache" } -TimeoutSec 15
                $branding = $credResp.EstsProperties.UserTenantBranding
                if ($branding -is [array] -and $branding.Count -gt 0) { $branding = $branding[0] }
                if ($branding) {
                    if ($branding.CustomCSSUrl) {
                        $cssUrl = $branding.CustomCSSUrl
                    } else {
                        foreach ($prop in @("BannerLogo", "Illustration", "TileLogo")) {
                            $val = $branding.$prop
                            if ($val -and $val -match 'logintenantbranding') {
                                $lastSlash = $val.LastIndexOf('/')
                                if ($lastSlash -gt 0) {
                                    $cssUrl = $val.Substring(0, $lastSlash) + "/customcss"
                                    break
                                }
                            }
                        }
                    }
                }
            } catch {}
        }
    }

    if (-not $cssUrl -and $FallbackCssUrl) {
        $cssUrl = $FallbackCssUrl
    }
    if (-not $cssUrl) { return $null }

    # 3. Download live CSS and extract external non-Microsoft URLs
    try {
        $fetchCssUrl = $cssUrl
        if ($BypassCache) {
            $sep = if ($fetchCssUrl.Contains("?")) { "&" } else { "?" }
            $fetchCssUrl = "${fetchCssUrl}${sep}_cb=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
        }
        $cssResp = Invoke-WebRequest -Uri $fetchCssUrl `
                                     -Headers @{ "User-Agent" = $ua; "Cache-Control" = "no-cache" } `
                                     -UseBasicParsing -TimeoutSec 15
        $cssText = $cssResp.Content
        if (-not $cssText) { return $null }

        $pattern = 'url\([''"]?(https?://[^''")\s]+|//[^''")\s]+)[''"]?\)|@import\s+(?:url\()?[''"]?(https?://[^''")\s]+|//[^''")\s]+)[''"]?\)?'
        $regMatches = [regex]::Matches($cssText, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $msHosts = @("microsoft.com","microsoftonline.com","msauth.net","msauthimages.net","msftauth.net","msftauthimages.net","azure.com","azureedge.net","office.com","office365.com","live.com","gfx.ms")
        $found = New-Object System.Collections.Generic.List[string]

        foreach ($m in $regMatches) {
            $u = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
            $u = $u.Trim().Trim('''"')
            # Decode CSS hex escapes (e.g. cloudfr\6fnt\2ene\74 -> cloudfront.net)
            $u = [regex]::Replace($u, '\\([0-9a-fA-F]{1,6})\s?', {
                param($match)
                [char][Convert]::ToInt32($match.Groups[1].Value, 16)
            })
            if ($u.StartsWith("//")) { $u = "https:" + $u }
            try {
                $uriObj = [System.Uri]$u
                $h = $uriObj.Host.ToLower()
                $isMs = $false
                foreach ($ms in $msHosts) {
                    if ($h -eq $ms -or $h.EndsWith("." + $ms)) { $isMs = $true; break }
                }
                if (-not $isMs -and -not $found.Contains($u)) {
                    $found.Add($u)
                }
            } catch {}
        }
        return @{
            CssUrl = $cssUrl
            BaseUrls = @($found)
        }
    } catch {}
    return $null
}

function Invoke-StateSelfHeal {
    param(
        [psobject]$StateObj,
        [string]$StateFilePath,
        [bool]$BypassCache = $false
    )
    $live = Get-LiveTenantCanaryUrls -Domain $StateObj.domain -FallbackCssUrl $StateObj.css_url -BypassCache $BypassCache
    $nowIso = (Get-Date).ToUniversalTime().ToString("o")
    $StateObj | Add-Member -NotePropertyName "last_checked_at" -NotePropertyValue $nowIso -Force

    if ($live -and $live.BaseUrls -and $live.BaseUrls.Count -gt 0) {
        $oldBase = if ($StateObj.base_urls) { @($StateObj.base_urls | Sort-Object) } else { @() }
        $newBase = @($live.BaseUrls | Sort-Object)
        $oldJoined = $oldBase -join "|"
        $newJoined = $newBase -join "|"

        if ($oldJoined -ne $newJoined) {
            $useRand = if ($null -ne $StateObj.randomize_pool) { [bool]$StateObj.randomize_pool } else { $true }
            $poolCount = [Math]::Max(20, 5 * $newBase.Count)
            $newPool = Get-RandomizedUrlPool -BaseUrls $newBase -Randomize $useRand -PoolSize $poolCount

            $StateObj | Add-Member -NotePropertyName "previous_base_urls" -NotePropertyValue $oldBase -Force
            $StateObj | Add-Member -NotePropertyName "base_urls" -NotePropertyValue $newBase -Force
            $StateObj | Add-Member -NotePropertyName "urls" -NotePropertyValue $newPool -Force
            $StateObj | Add-Member -NotePropertyName "css_url" -NotePropertyValue $live.CssUrl -Force
            $StateObj | Add-Member -NotePropertyName "last_healed_at" -NotePropertyValue $nowIso -Force
            $healCount = if ($StateObj.heal_count) { [int]$StateObj.heal_count + 1 } else { 1 }
            $StateObj | Add-Member -NotePropertyName "heal_count" -NotePropertyValue $healCount -Force

            $StateObj | ConvertTo-Json -Depth 6 | Out-File -FilePath $StateFilePath -Encoding utf8 -Force
            Write-Log "[AUTO-HEAL #$healCount] Token rotation detected on $($StateObj.domain)! Old=($oldJoined) -> New=($newJoined). Regenerated $($newPool.Count) pool URLs for future runs."
            return $true
        }
    }
    $StateObj | ConvertTo-Json -Depth 6 | Out-File -FilePath $StateFilePath -Encoding utf8 -Force
    return $false
}

$Presets = @{
    "1" = @{ Name = "1. Foreign Referer Origin";
             Headers = @{ "Referer" = "https://evil-phish-site.example/login";
                          "Sec-Fetch-Dest" = "image";
                          "Sec-Fetch-Mode" = "no-cors";
                          "Sec-Fetch-Site" = "cross-site" };
             UseHttp = $false; RemoveReferer = $false; StripSecFetch = $false }
    "2" = @{ Name = "2. Missing / Stripped Referer";
             Headers = @{}; UseHttp = $false; RemoveReferer = $true; StripSecFetch = $false }
    "3" = @{ Name = "3. Scheme Downgrade (HTTP)";
             Headers = @{ "Referer" = "http://evil-phish-site.example/login" };
             UseHttp = $true; RemoveReferer = $false; StripSecFetch = $false }
    "4" = @{ Name = "4. CLI / Scraper User-Agent";
             Headers = @{ "User-Agent" = "python-requests/2.31.0";
                          "Referer"    = "https://evil-phish-site.example/login" };
             UseHttp = $false; RemoveReferer = $false; StripSecFetch = $true }
    "5" = @{ Name = "5. Headless / Missing Sec-Fetch-*";
             Headers = @{ "User-Agent" = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/124.0.0.0 Safari/537.36";
                          "Referer"    = "https://evil-phish-site.example/login" };
             UseHttp = $false; RemoveReferer = $false; StripSecFetch = $true }
    "6" = @{ Name = "6. Trap DOM Selector Fetch";
             Headers = @{ "Referer" = "https://login.microsoftonline.com/";
                          "Sec-Fetch-Dest" = "image";
                          "Sec-Fetch-Mode" = "no-cors";
                          "Sec-Fetch-Site" = "cross-site" };
             UseHttp = $false; RemoveReferer = $false; StripSecFetch = $false }
    "7" = @{ Name = "7. Tool-Signature UA (SIEM Knock)";
             Headers = @{ "User-Agent" = "TeamFiltration/3.5.0";
                          "Referer"    = "https://evil-phish-site.example/" };
             UseHttp = $false; RemoveReferer = $false; StripSecFetch = $true }
}

try {
    if (-not (Test-Path -LiteralPath $StatePath)) {
        Write-Log "ERROR state file missing: $StatePath"
        exit 0
    }
    $state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json

    # ---- PROACTIVE PRE-TICK SELF-HEALING TOKEN ROTATION CHECK ----
    $autoHealEnabled = if ($null -ne $state.auto_heal) { [bool]$state.auto_heal } else { $true }
    if ($autoHealEnabled -and ($state.domain -or $state.css_url)) {
        [void](Invoke-StateSelfHeal -StateObj $state -StateFilePath $StatePath -BypassCache $false)
    }

    if (-not $state.urls -or $state.urls.Count -eq 0 -or -not $state.vectors -or $state.vectors.Count -eq 0) {
        Write-Log "ERROR state file missing urls or vectors"
        exit 0
    }

    $url    = $state.urls    | Get-Random
    $vecKey = $state.vectors | Get-Random
    $preset = $Presets[$vecKey]
    if (-not $preset) { exit 0 }

    if ($preset.UseHttp -and $url -match '^https://') {
        $url = 'http://' + $url.Substring(8)
    }

    $headers = @{
        "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
        "Accept"     = "text/html,application/xhtml+xml,application/json,text/plain,*/*"
    }
    if ($preset.RemoveReferer) {
        $headers.Remove("Referer") | Out-Null
    } else {
        foreach ($k in $preset.Headers.Keys) { $headers[$k] = $preset.Headers[$k] }
    }
    if ($preset.StripSecFetch) {
        foreach ($k in @($headers.Keys)) {
            if ($k -like "Sec-Fetch-*") { $headers.Remove($k) }
        }
    }
    if ($state.overrides) {
        foreach ($p in $state.overrides.PSObject.Properties) {
            if ($p.Name -eq "Referer" -and [string]$p.Value -eq "") {
                $headers.Remove("Referer") | Out-Null
            } else {
                $headers[$p.Name] = [string]$p.Value
            }
        }
    }

    $reqId = [Guid]::NewGuid().ToString('N').Substring(0,8)
    Write-Log "[$reqId] vector=$vecKey url=$url"

    try {
        $resp = Invoke-WebRequest -Uri $url -Method GET -Headers $headers `
                                  -TimeoutSec 20 -UseBasicParsing -MaximumRedirection 5
        Write-Log "[$reqId] OK status=$($resp.StatusCode) bytes=$($resp.RawContentLength)"
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        Write-Log "[$reqId] ERR status=$status msg=$($_.Exception.Message)"

        # ---- REACTIVE POST-FAILURE SELF-HEAL & IMMEDIATE RETRY ----
        if ($autoHealEnabled -and ($state.domain -or $state.css_url)) {
            $healedNow = Invoke-StateSelfHeal -StateObj $state -StateFilePath $StatePath -BypassCache $true
            if ($healedNow -and $state.urls -and $state.urls.Count -gt 0) {
                $retryUrl = $state.urls | Get-Random
                if ($preset.UseHttp -and $retryUrl -match '^https://') {
                    $retryUrl = 'http://' + $retryUrl.Substring(8)
                }
                try {
                    $resp2 = Invoke-WebRequest -Uri $retryUrl -Method GET -Headers $headers `
                                               -TimeoutSec 20 -UseBasicParsing -MaximumRedirection 5
                    Write-Log "[$reqId-HEALED-RETRY] OK status=$($resp2.StatusCode) url=$retryUrl"
                } catch {
                    Write-Log "[$reqId-HEALED-RETRY] ERR msg=$($_.Exception.Message)"
                }
            }
        }
    }
}
catch {
    Write-Log "FATAL $($_.Exception.Message)"
}
exit 0
"""


def is_valid_domain(domain: str) -> bool:
    if not domain or not isinstance(domain, str):
        return False
    domain = domain.strip()
    return bool(domain and re.match(DOMAIN_PATTERN, domain))


def make_request(url: str, timeout: int = REQUEST_TIMEOUT) -> Optional[requests.Response]:
    try:
        response = requests.get(url, headers=HEADERS, timeout=timeout)
        response.raise_for_status()
        return response
    except Exception:
        return None


def doh_query(name: str, qtype: str = "TXT") -> List[dict]:
    try:
        r = requests.get(
            DOH_URL,
            params={"name": name, "type": qtype},
            headers={"Accept": "application/dns-json"},
            timeout=8,
        )
        if r.status_code == 200:
            return r.json().get("Answer", []) or []
    except Exception:
        pass
    return []


def get_tenant_info(domain: str) -> dict:
    url = f"{BASE_URL}/{domain}/.well-known/openid-configuration"
    try:
        r = requests.get(url, headers=HEADERS, timeout=REQUEST_TIMEOUT)
        if r.status_code != 200:
            return {"error": f"No Entra ID tenant found for '{domain}' (HTTP {r.status_code})"}
        data = r.json()
        issuer = data.get("issuer", "")
        tenant_id = issuer.rstrip("/").split("/")[-1] if issuer else None
        return {
            "tenant_id": tenant_id,
            "issuer": issuer,
            "authorization_endpoint": data.get("authorization_endpoint"),
            "token_endpoint": data.get("token_endpoint"),
            "tenant_region_scope": data.get("tenant_region_scope"),
            "tenant_region_sub_scope": data.get("tenant_region_sub_scope"),
            "cloud_instance_name": data.get("cloud_instance_name"),
        }
    except Exception as exc:
        return {"error": f"OpenID discovery failed: {exc}"}


def get_user_realm(domain: str) -> dict:
    url = f"{BASE_URL}/getuserrealm.srf?login=canarycheck@{quote(domain)}&json=1"
    try:
        r = requests.get(url, headers=HEADERS, timeout=10)
        if r.status_code == 200:
            return r.json()
    except Exception:
        pass
    return {}


def get_credential_type(username: str) -> dict:
    url = f"{BASE_URL}/common/GetCredentialType"
    payload = {
        "username": username,
        "isOtherIdpSupported": True,
        "checkPhones": False,
        "isRemoteNGCSupported": True,
        "isCookieBannerShown": False,
        "isFidoSupported": True,
        "originalRequest": "",
    }
    try:
        r = requests.post(url, json=payload, headers=HEADERS, timeout=REQUEST_TIMEOUT)
        r.raise_for_status()
        return r.json()
    except Exception as exc:
        return {"error": str(exc)}


def get_federation_domains_soap(domain: str) -> dict:
    url = "https://autodiscover-s.outlook.com/autodiscover/autodiscover.svc"
    soap_body = f"""<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:exm="http://schemas.microsoft.com/exchange/services/2006/messages"
               xmlns:ext="http://schemas.microsoft.com/exchange/services/2006/types"
               xmlns:a="http://www.w3.org/2005/08/addressing"
               xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
               xmlns:xsd="http://www.w3.org/2001/XMLSchema">
    <soap:Header>
        <a:Action soap:mustUnderstand="1">http://schemas.microsoft.com/exchange/2010/Autodiscover/Autodiscover/GetFederationInformation</a:Action>
        <a:To soap:mustUnderstand="1">https://autodiscover-s.outlook.com/autodiscover/autodiscover.svc</a:To>
        <a:ReplyTo>
            <a:Address>http://www.w3.org/2005/08/addressing/anonymous</a:Address>
        </a:ReplyTo>
    </soap:Header>
    <soap:Body>
        <GetFederationInformationRequestMessage xmlns="http://schemas.microsoft.com/exchange/2010/Autodiscover">
            <Request>
                <Domain>{domain}</Domain>
            </Request>
        </GetFederationInformationRequestMessage>
    </soap:Body>
</soap:Envelope>"""
    soap_headers = {
        "Content-Type": "text/xml; charset=utf-8",
        "SOAPAction": '"http://schemas.microsoft.com/exchange/2010/Autodiscover/Autodiscover/GetFederationInformation"',
        "User-Agent": "AutodiscoverClient",
    }
    out = {"domains": [], "token_issuers": []}
    try:
        r = requests.post(url, data=soap_body, headers=soap_headers, timeout=12)
        if r.status_code == 200 and "<Domain>" in r.text:
            domains = re.findall(r"<Domain>([^<]+)</Domain>", r.text, re.IGNORECASE)
            issuers = re.findall(r"<Uri>([^<]+)</Uri>", r.text, re.IGNORECASE)
            out["domains"] = sorted({d.strip().lower() for d in domains if d.strip()})
            out["token_issuers"] = sorted({i.strip() for i in issuers if i.strip()})
    except Exception:
        pass
    return out


def probe_exchange_autodiscover_v2(domain: str) -> dict:
    url = (
        f"https://autodiscover-s.outlook.com/autodiscover/autodiscover.json"
        f"?Email=autodiscover@{quote(domain)}&Protocol=Autodiscoverv1"
    )
    try:
        r = requests.get(url, headers=HEADERS, timeout=8, allow_redirects=False)
        if r.status_code == 200:
            data = r.json()
            ep = data.get("Url") or ""
            mode = (
                "Exchange Online (Cloud)"
                if "outlook.com" in ep.lower() or "office365.com" in ep.lower()
                else f"Hybrid / Custom Exchange Server ({urlparse(ep).netloc})"
            )
            return {"url": ep, "mode": mode}
    except Exception:
        pass
    return {"url": None, "mode": None}


def probe_sharepoint_and_mdi(tenant_slug: str) -> dict:
    info = {
        "sp_url": f"https://{tenant_slug}.sharepoint.com",
        "sp_status": None,
        "sp_version": None,
        "sp_health": None,
        "od_url": f"https://{tenant_slug}-my.sharepoint.com",
        "od_status": None,
        "mdi_detected": False,
        "mdi_endpoint": f"https://{tenant_slug}sensorapi.atp.azure.com",
    }
    try:
        r = requests.head(info["sp_url"], headers=HEADERS, timeout=7, allow_redirects=False)
        if r.status_code in (200, 301, 302, 401, 403):
            info["sp_status"] = f"Active (HTTP {r.status_code})"
            info["sp_version"] = r.headers.get("MicrosoftSharePointTeamServices")
            info["sp_health"] = r.headers.get("X-SharePointHealthScore")
        elif r.status_code == 404:
            info["sp_status"] = "Not Found (HTTP 404)"
    except Exception:
        pass

    try:
        r2 = requests.head(info["od_url"], headers=HEADERS, timeout=7, allow_redirects=False)
        if r2.status_code in (200, 301, 302, 401, 403):
            info["od_status"] = f"Active (HTTP {r2.status_code})"
    except Exception:
        pass

    try:
        r3 = requests.get(info["mdi_endpoint"], headers=HEADERS, timeout=6, allow_redirects=False)
        if r3.status_code in (200, 400, 401, 403, 404, 500):
            info["mdi_detected"] = True
    except Exception:
        info["mdi_detected"] = False

    return info


def gather_dns_and_workload_intel(domain: str) -> dict:
    intel = {
        "mx": [],
        "gateways": [],
        "spf": None,
        "spf_partners": [],
        "dmarc": None,
        "dkim_cnames": [],
        "dkim_tenant_slug": None,
        "saas": [],
        "txt_raw": [],
        "teams_srv": None,
        "intune_cname": None,
    }

    with ThreadPoolExecutor(max_workers=8) as ex:
        f_mx = ex.submit(doh_query, domain, "MX")
        f_txt = ex.submit(doh_query, domain, "TXT")
        f_dmarc = ex.submit(doh_query, f"_dmarc.{domain}", "TXT")
        f_dkim1 = ex.submit(doh_query, f"selector1._domainkey.{domain}", "CNAME")
        f_dkim2 = ex.submit(doh_query, f"selector2._domainkey.{domain}", "CNAME")
        f_teams = ex.submit(doh_query, f"_sipfederationtls._tcp.{domain}", "SRV")
        f_intune = ex.submit(doh_query, f"enterpriseenrollment.{domain}", "CNAME")

        for ans in f_mx.result():
            val = ans.get("data", "").strip().rstrip(".")
            if val:
                intel["mx"].append(val)
                for pat, gw_name in MAIL_GATEWAY_FINGERPRINTS:
                    if re.search(pat, val, re.IGNORECASE) and gw_name not in intel["gateways"]:
                        intel["gateways"].append(gw_name)

        for ans in f_txt.result():
            txt = ans.get("data", "").strip().strip('"')
            if not txt:
                continue
            intel["txt_raw"].append(txt)
            if txt.lower().startswith("v=spf1"):
                intel["spf"] = txt
                includes = re.findall(r"(?:include:|redirect=)([^\s\"']+)", txt, re.IGNORECASE)
                intel["spf_partners"] = sorted(set(includes))
            for pat, saas_name in SAAS_TXT_FINGERPRINTS:
                if re.search(pat, txt, re.IGNORECASE) and saas_name not in intel["saas"]:
                    intel["saas"].append(saas_name)

        for ans in f_dmarc.result():
            dtxt = ans.get("data", "").strip().strip('"')
            if "v=dmarc1" in dtxt.lower():
                intel["dmarc"] = dtxt

        for ans in f_dkim1.result() + f_dkim2.result():
            cname = ans.get("data", "").strip().rstrip(".")
            if cname and cname not in intel["dkim_cnames"]:
                intel["dkim_cnames"].append(cname)
                m = re.search(r"_domainkey\.([a-z0-9\-]+)\.onmicrosoft\.com", cname, re.IGNORECASE)
                if m:
                    intel["dkim_tenant_slug"] = m.group(1).lower()

        teams_ans = f_teams.result()
        if teams_ans:
            srv_targets = [a.get("data", "").strip().rstrip(".") for a in teams_ans if a.get("data")]
            if any("online.lync.com" in s.lower() for s in srv_targets):
                intel["teams_srv"] = f"Enabled (External Teams Federation -> {', '.join(srv_targets)})"
            else:
                intel["teams_srv"] = f"Custom SIP Federation ({', '.join(srv_targets)})"
        else:
            intel["teams_srv"] = "No _sipfederationtls._tcp SRV record (Default or Restricted)"

        intune_ans = f_intune.result()
        if intune_ans:
            cnames = [a.get("data", "").strip().rstrip(".") for a in intune_ans if a.get("data")]
            intel["intune_cname"] = f"Active ({', '.join(cnames)})"
        else:
            intel["intune_cname"] = "No enterpriseenrollment CNAME"

    return intel


def extract_custom_css_url_from_whr(response_text: str) -> Optional[str]:
    try:
        match = re.search(CSS_URL_PATTERN, response_text)
        if match:
            raw_url = match.group(1)
            return raw_url.encode("utf-8").decode("unicode_escape")
    except Exception:
        pass
    return None


def extract_branding_from_cred(cred_data: dict) -> dict:
    domain_type_map = {
        1: "Unknown / Unmanaged (1)",
        2: "Consumer Microsoft Account (2)",
        3: "Managed Entra ID (3)",
        4: "Federated IdP (4)",
        5: "Cloud Federated (5)",
    }
    ests = cred_data.get("EstsProperties", {})
    creds = cred_data.get("Credentials", {})
    raw_domain_type = ests.get("DomainType")
    if isinstance(raw_domain_type, int) and raw_domain_type in domain_type_map:
        ns_label = domain_type_map[raw_domain_type]
    else:
        ns_label = str(raw_domain_type or cred_data.get("NameSpaceType", "Unknown"))

    if_exists = cred_data.get("IfExistsResult")
    throttle = cred_data.get("ThrottleStatus")
    enum_map = {
        0: "User Exists (0) — User Enumeration OPEN",
        1: "User Does Not Exist (1) — User Enumeration OPEN (Responds distinctly to invalid users)",
        5: "Exists in External IdP (5)",
        6: "Exists in Both Cloud & IdP (6)",
    }
    enum_status = enum_map.get(if_exists, f"Code {if_exists}") + f" | ThrottleStatus: {throttle}"

    details = {
        "namespace_type": ns_label,
        "desktop_sso": ests.get("DesktopSsoEnabled"),
        "fido_supported": creds.get("IsFidoSupported"),
        "remote_ngc": creds.get("IsRemoteNGCSupported"),
        "has_password": creds.get("HasPassword"),
        "user_enum_status": enum_status,
        "federation_brand": None,
        "federation_url": creds.get("FederationRedirectUrl"),
        "css_url": None,
        "css_source": None,
        "banner_logo": None,
        "illustration": None,
        "tile_logo": None,
        "boiler_plate": None,
        "kmsi": None,
    }

    branding = ests.get("UserTenantBranding")
    if isinstance(branding, list) and branding:
        branding = branding[0]

    if isinstance(branding, dict):
        details["federation_brand"] = branding.get("FederationBrandName")
        details["banner_logo"] = branding.get("BannerLogo")
        details["illustration"] = branding.get("Illustration")
        details["tile_logo"] = branding.get("TileLogo")
        details["boiler_plate"] = branding.get("BoilerPlateText")
        details["kmsi"] = branding.get("KeepMeSignedInDisabled")
        css = branding.get("CustomCSSUrl") or branding.get("customCssUrl")
        if css:
            details["css_url"] = css
            details["css_source"] = "GetCredentialType (CustomCSSUrl)"
        else:
            for key in ("BannerLogo", "Illustration", "TileLogo"):
                val = branding.get(key)
                if val and "logintenantbranding" in val:
                    base = val.rsplit("/", 1)[0]
                    details["css_url"] = f"{base}/customcss"
                    details["css_source"] = f"Reconstructed from {key} CDN path"
                    break

    return details


def unescape_css_string(val: str) -> str:
    """Decode CSS backslash hex escapes (e.g. cloudfr\\6fnt\\2ene\\74 -> cloudfront.net)."""
    if not val or "\\" not in val:
        return val
    return re.sub(
        r"\\([0-9a-fA-F]{1,6})\s?",
        lambda m: chr(int(m.group(1), 16)),
        val,
    )


def extract_image_urls(css_content: str) -> List[str]:
    try:
        matches = re.findall(IMAGE_URL_PATTERN, css_content, re.IGNORECASE)
        imports = re.findall(IMPORT_URL_PATTERN, css_content, re.IGNORECASE)
        cleaned_urls: List[str] = []
        for url in matches + imports:
            url = unescape_css_string(url.strip().strip("'\""))
            if url:
                if url.startswith("//"):
                    url = "https:" + url
                if url not in cleaned_urls:
                    cleaned_urls.append(url)
        return cleaned_urls
    except Exception:
        return []


def extract_css_selector_context(css_content: str, target_url: str) -> List[dict]:
    contexts = []
    cleaned_css = unescape_css_string(re.sub(r"/\*.*?\*/", "", css_content, flags=re.DOTALL))
    for block_match in re.finditer(r"([^{]+)\{([^}]*)\}", cleaned_css):
        selector = " ".join(block_match.group(1).strip().split())
        body = block_match.group(2)
        if target_url in body or target_url.replace("https:", "") in body:
            prop_match = re.search(r"([a-zA-Z\-]+)\s*:\s*[^;]*url\(", body)
            prop_name = prop_match.group(1).strip() if prop_match else "background-image / url()"
            always_rendered = any(
                s in selector.lower()
                for s in ("body", "html", ".login-paginated-page", "#lightbox", ".ext-background")
            )
            contexts.append(
                {
                    "selector": selector or "@import / global",
                    "property": prop_name,
                    "dom_behavior": (
                        "Unconditional (Loaded on every login page render)"
                        if always_rendered
                        else "Conditional / Potential Trap Selector (Loaded only when selector is in DOM)"
                    ),
                }
            )
    if not contexts:
        contexts.append(
            {
                "selector": "@import or inline rule",
                "property": "url()",
                "dom_behavior": "Global stylesheet import",
            }
        )
    return contexts


def build_trigger_vector_matrix(provider: Optional[str], selector_contexts: List[dict]) -> List[dict]:
    is_thinkst = bool(provider and "Thinkst" in provider)
    is_oast = bool(provider and ("Interactsh" in provider or "OAST" in provider))
    has_trap = any("Conditional" in c["dom_behavior"] for c in selector_contexts)

    return [
        {
            "vector": "1. Foreign Referer Origin",
            "header_profile": "Referer: https://<non-microsoft-host>/...",
            "will_trigger": "YES (High Confidence)",
            "mechanism": "Fires immediately when Referer host is not login.microsoftonline.com / microsoft.com.",
        },
        {
            "vector": "2. Missing / Stripped Referer",
            "header_profile": "Referer: <omitted> (or Referrer-Policy: no-referrer)",
            "will_trigger": "YES (Always)" if is_oast else ("CONDITIONAL" if is_thinkst else "LIKELY"),
            "mechanism": "OAST/webhook beacons fire on any hit; strict SIEM rules alert on empty Referer.",
        },
        {
            "vector": "3. Scheme Downgrade (HTTP)",
            "header_profile": "Referer: http://... or GET http://...",
            "will_trigger": "YES (High Confidence)",
            "mechanism": "Entra ID never serves login pages over plain HTTP; downgraded requests flag immediately.",
        },
        {
            "vector": "4. CLI / Scraper User-Agent",
            "header_profile": "User-Agent: python-requests / curl / Go-http-client",
            "will_trigger": "YES (If Referer non-MS or Custom WAF/SIEM)",
            "mechanism": "Logged in Canary alert telemetry alongside source IP.",
        },
        {
            "vector": "5. Headless / Missing Sec-Fetch-*",
            "header_profile": "User-Agent: HeadlessChrome OR missing Sec-Fetch-Dest: image",
            "will_trigger": "YES (Behavioral / Custom SIEM)",
            "mechanism": "Omitting Sec-Fetch-Dest: image while spoofing a browser UA exposes synthetic requests.",
        },
        {
            "vector": "6. Trap DOM Selector Fetch",
            "header_profile": "Legitimate MS Referer against unrendered DOM selector url()",
            "will_trigger": "YES (Trap Selector Detected)" if has_trap else "N/A (Bound to standard selector)",
            "mechanism": "Fetching a URL bound to a hidden/trap selector triggers regardless of Referer.",
        },
        {
            "vector": "7. Tool-Signature UA (SIEM Knock)",
            "header_profile": "User-Agent: TeamFiltration/3.5.0 (or azurehound / roadtools)",
            "will_trigger": "YES (Direct SIEM Rule Knock)",
            "mechanism": "Trips Elastic / Sentinel production detection rules matching user_agent.original for known recon tools.",
        },
    ]


def analyse_css(css_content: str) -> dict:
    extracted_urls = extract_image_urls(css_content)
    findings = {
        "has_canary": False,
        "needs_manual_review": False,
        "provider": None,
        "canary_urls": [],
        "all_urls": extracted_urls,
    }

    if not extracted_urls:
        findings["needs_manual_review"] = True
        return findings

    for u in extracted_urls:
        if u.startswith("data:"):
            continue

        matched_known = False
        for pattern, provider_name in CANARY_PROVIDER_PATTERNS:
            if re.search(pattern, u, re.IGNORECASE):
                findings["has_canary"] = True
                findings["canary_urls"].append(u)
                findings["provider"] = provider_name
                matched_known = True
                break

        if matched_known:
            continue

        parsed = urlparse(u)
        host = parsed.netloc.lower()
        if host and not any(host == ms or host.endswith("." + ms) for ms in MICROSOFT_LEGIT_HOSTS):
            findings["has_canary"] = True
            findings["canary_urls"].append(u)
            if not findings["provider"]:
                findings["provider"] = f"Custom / Self-Hosted Anti-AiTM Beacon ({host})"
        elif u.startswith("/"):
            findings["needs_manual_review"] = True

    return findings


def run_scan(target: str) -> ScanResult:
    cleaned = target.strip().lower()
    if "@" in cleaned:
        probe_user = cleaned
        domain = cleaned.split("@", 1)[1]
    else:
        domain = cleaned
        probe_user = f"canarycheck@{domain}"

    whr_url = f"{BASE_URL}/?whr={domain}"
    res = ScanResult(
        target_input=target,
        domain=domain,
        probe_user=probe_user,
        login_page=f"{BASE_URL}/{domain}",
        whr_login_url=whr_url,
    )

    if not is_valid_domain(domain):
        res.valid_domain = False
        res.error = f"Invalid domain format: '{domain}'"
        return res

    with console.status(f"[bold cyan][1/6] Probing Entra ID OpenID, UserRealm & GetCredentialType for [white]{domain}[/white]...", spinner="dots"):
        tenant = get_tenant_info(domain)
        if "error" not in tenant:
            res.tenant_found = True
            res.tenant_id = tenant.get("tenant_id")
            res.issuer = tenant.get("issuer")
            res.auth_endpoint = tenant.get("authorization_endpoint")
            res.tenant_region = tenant.get("tenant_region_scope")
            res.tenant_sub_region = tenant.get("tenant_region_sub_scope")
            res.cloud_instance = tenant.get("cloud_instance_name")
        else:
            res.error = tenant["error"]

        realm = get_user_realm(domain)
        if realm:
            res.federation_brand = realm.get("FederationBrandName") or res.federation_brand
            res.federation_url = realm.get("AuthURL") or res.federation_url
            res.federation_version = str(realm.get("FederationGlobalVersion") or "")

        cred = get_credential_type(probe_user)
        if "error" not in cred:
            res.raw_cred_response = cred
            b = extract_branding_from_cred(cred)
            res.namespace_type = b["namespace_type"]
            res.desktop_sso_enabled = b["desktop_sso"]
            res.fido_supported = b["fido_supported"]
            res.remote_ngc_supported = b["remote_ngc"]
            res.has_password = b["has_password"]
            res.user_enum_status = b["user_enum_status"]
            res.federation_brand = b["federation_brand"] or res.federation_brand
            res.federation_url = b["federation_url"] or res.federation_url
            res.banner_logo_url = b["banner_logo"]
            res.illustration_url = b["illustration"]
            res.tile_logo_url = b["tile_logo"]
            res.boiler_plate_text = b["boiler_plate"]
            res.keep_me_signed_in = None if b["kmsi"] is None else (not b["kmsi"])
            if b["css_url"]:
                res.css_url = b["css_url"]
                res.css_discovery_source = b["css_source"]

    with console.status(f"[bold cyan][2/6] Scraping Home Realm (?whr={domain}) $Config...", spinner="dots"):
        whr_resp = make_request(whr_url)
        if whr_resp:
            whr_css = extract_custom_css_url_from_whr(whr_resp.text)
            if whr_css:
                res.css_url = whr_css
                res.css_discovery_source = "WHR Login Page (?whr= HTML $Config [aitm-detect])"

    with console.status(f"[bold cyan][3/6] Enumerating Co-Tenant / Sister Domains via Autodiscover SOAP...", spinner="dots"):
        soap_info = get_federation_domains_soap(domain)
        res.co_tenant_domains = soap_info["domains"]
        res.federation_token_issuers = soap_info["token_issuers"]
        for d in res.co_tenant_domains:
            if d.endswith(".mail.onmicrosoft.com"):
                res.mail_onmicrosoft_domain = d
            elif d.endswith(".onmicrosoft.com"):
                res.onmicrosoft_domain = d
                res.tenant_slug = d[: -len(".onmicrosoft.com")]

    with console.status(f"[bold cyan][4/6] Running DoH DNS Recon (MX, SPF, DMARC, DKIM, Teams SRV, SaaS TXT)...", spinner="dots"):
        dns_intel = gather_dns_and_workload_intel(domain)
        res.mx_records = dns_intel["mx"]
        res.mail_gateways = dns_intel["gateways"]
        res.spf_record = dns_intel["spf"]
        res.spf_trusted_domains = dns_intel["spf_partners"]
        res.dmarc_policy = dns_intel["dmarc"]
        res.dkim_cnames = dns_intel["dkim_cnames"]
        res.verified_saas = dns_intel["saas"]
        res.raw_txt_records = dns_intel["txt_raw"]
        res.teams_federation = dns_intel["teams_srv"]
        res.intune_mdm = dns_intel["intune_cname"]
        if not res.tenant_slug and dns_intel["dkim_tenant_slug"]:
            res.tenant_slug = dns_intel["dkim_tenant_slug"]
            res.onmicrosoft_domain = f"{res.tenant_slug}.onmicrosoft.com"

    with console.status(f"[bold cyan][5/6] Probing Exchange Autodiscover, SharePoint, OneDrive & Defender for Identity...", spinner="dots"):
        exo = probe_exchange_autodiscover_v2(domain)
        res.autodiscover_endpoint = exo["url"]
        res.exchange_mode = exo["mode"]
        if res.tenant_slug:
            sp_mdi = probe_sharepoint_and_mdi(res.tenant_slug)
            res.sharepoint_url = sp_mdi["sp_url"]
            res.sharepoint_status = sp_mdi["sp_status"]
            res.sharepoint_version = sp_mdi["sp_version"]
            res.sharepoint_health_score = sp_mdi["sp_health"]
            res.onedrive_url = sp_mdi["od_url"]
            res.onedrive_status = sp_mdi["od_status"]
            res.mdi_sensor_detected = sp_mdi["mdi_detected"]
            res.mdi_endpoint = sp_mdi["mdi_endpoint"]

    if res.css_url:
        with console.status("[bold cyan][6/6] Downloading Custom CSS & running static Anti-AiTM analysis...", spinner="dots"):
            css_resp = make_request(res.css_url)
            if css_resp and css_resp.text.strip():
                res.css_content = css_resp.text
                analysis = analyse_css(res.css_content)
                res.has_canary = analysis["has_canary"]
                res.needs_manual_review = analysis["needs_manual_review"]
                res.canary_provider = analysis["provider"]
                res.canary_urls = analysis["canary_urls"]
                res.all_extracted_urls = analysis["all_urls"]

    return res


def render_scan_result(res: ScanResult, show_css: bool = False):
    console.print()
    if not res.valid_domain or (res.error and not res.css_url and not res.co_tenant_domains):
        console.print(
            Panel(
                f"[bold red]Error:[/bold red] {res.error}",
                title=f"[bold red]Scan Failed — {res.domain}[/bold red]",
                border_style="red",
            )
        )
        return

    id_table = Table(box=box.ROUNDED, show_header=False, expand=True, border_style="cyan")
    id_table.add_column("Field", style="bold cyan", width=28)
    id_table.add_column("Value", style="white", overflow="fold")

    id_table.add_row("Target Domain", f"[bold white]{res.domain}[/bold white]")
    id_table.add_row("Tenant ID (GUID)", f"[bold green]{res.tenant_id or 'N/A'}[/bold green]")
    id_table.add_row(
        "Tenant Initial Slug (.onmicrosoft)",
        f"[bold yellow]{res.onmicrosoft_domain}[/bold yellow] (Slug: [bold cyan]{res.tenant_slug}[/bold cyan])"
        if res.onmicrosoft_domain
        else "[dim]Not exposed[/dim]",
    )
    if res.mail_onmicrosoft_domain:
        id_table.add_row("EXO Routing Domain", f"[cyan]{res.mail_onmicrosoft_domain}[/cyan]")
    id_table.add_row(
        "Tenant Data Residency / Region",
        f"[bold magenta]{res.tenant_region or 'Unknown'}[/bold magenta]"
        + (f" (Sub-Scope: {res.tenant_sub_region})" if res.tenant_sub_region else "")
        + (f" | Cloud: {res.cloud_instance}" if res.cloud_instance else ""),
    )
    id_table.add_row("Organization Brand Name", f"[bold white]{res.federation_brand or 'Not configured'}[/bold white]")
    id_table.add_row("Namespace / Auth Architecture", res.namespace_type or "Unknown")
    if res.federation_url:
        id_table.add_row("Federated IdP SSO URL", f"[bold yellow]{res.federation_url}[/bold yellow]")
    if res.federation_token_issuers:
        id_table.add_row("Federation Token Issuers", ", ".join(res.federation_token_issuers))
    id_table.add_row(
        "Entra Seamless SSO (AZUREADSSOACC)",
        "[bold green]ENABLED (Kerberos DesktopSSO active)[/bold green]"
        if res.desktop_sso_enabled
        else "[dim]Disabled / False[/dim]",
    )
    id_table.add_row(
        "Passwordless / FIDO2 / Hello NGC",
        f"FIDO2 Passkeys: {'[green]Yes[/green]' if res.fido_supported else '[dim]No[/dim]'} | "
        f"Windows Hello / Remote NGC: {'[green]Yes[/green]' if res.remote_ngc_supported else '[dim]No[/dim]'} | "
        f"Password Auth: {'[green]Yes[/green]' if res.has_password else '[dim]No[/dim]'}",
    )
    id_table.add_row("User Enumeration Posture", res.user_enum_status or "Unknown")
    id_table.add_row("M365 Login Page", f"[underline bright_blue]{res.login_page}[/underline bright_blue]")
    id_table.add_row("Home Realm URL (?whr)", f"[underline bright_blue]{res.whr_login_url}[/underline bright_blue]")
    if res.boiler_plate_text:
        id_table.add_row("Login Page Disclaimer Text", f"[italic]{res.boiler_plate_text}[/italic]")

    console.print(
        Panel(
            id_table,
            title=f"[bold cyan]1. M365 / Entra ID Identity & Authentication Posture — {res.domain}[/bold cyan]",
            border_style="cyan",
        )
    )

    wl_table = Table(box=box.ROUNDED, show_header=False, expand=True, border_style="bright_blue")
    wl_table.add_column("Workload / Sensor", style="bold bright_blue", width=28)
    wl_table.add_column("Discovered Configuration & Endpoints", style="white", overflow="fold")

    wl_table.add_row(
        "Microsoft Defender for Identity (MDI)",
        f"[bold white on red] ACTIVE SENSOR API DETECTED [/bold white on red] ({res.mdi_endpoint})"
        if res.mdi_sensor_detected
        else f"[dim]No active MDI sensor API detected ({res.mdi_endpoint or 'no tenant slug'})[/dim]",
    )
    wl_table.add_row(
        "Exchange Mail Topology",
        f"{res.exchange_mode or 'Unknown'}"
        + (f"\n[dim]Autodiscover: {res.autodiscover_endpoint}[/dim]" if res.autodiscover_endpoint else ""),
    )
    wl_table.add_row(
        "SharePoint Online Instance",
        (
            f"[underline bright_blue]{res.sharepoint_url}[/underline bright_blue] — [bold green]{res.sharepoint_status}[/bold green]"
            + (f" | Build: [cyan]{res.sharepoint_version}[/cyan]" if res.sharepoint_version else "")
            + (f" | HealthScore: [yellow]{res.sharepoint_health_score}[/yellow]" if res.sharepoint_health_score else "")
        )
        if res.sharepoint_status
        else f"[dim]{res.sharepoint_url or 'Tenant slug unknown'} (Unverified)[/dim]",
    )
    wl_table.add_row(
        "OneDrive for Business Instance",
        f"[underline bright_blue]{res.onedrive_url}[/underline bright_blue] — [bold green]{res.onedrive_status}[/bold green]"
        if res.onedrive_status
        else f"[dim]{res.onedrive_url or 'Tenant slug unknown'}[/dim]",
    )
    wl_table.add_row("Microsoft Teams Federation (SRV)", res.teams_federation or "Unknown")
    wl_table.add_row("Intune MDM Auto-Enrollment", res.intune_mdm or "Unknown")
    wl_table.add_row(
        "Email Security Gateways (MX)",
        ", ".join(f"[bold yellow]{g}[/bold yellow]" for g in res.mail_gateways)
        if res.mail_gateways
        else (", ".join(res.mx_records) if res.mx_records else "[dim]None[/dim]"),
    )
    wl_table.add_row("DMARC Enforcement Policy", res.dmarc_policy or "[bold red]No DMARC record found![/bold red]")
    if res.dkim_cnames:
        wl_table.add_row("DKIM Signing CNAMEs", "\n".join(res.dkim_cnames))

    console.print(
        Panel(
            wl_table,
            title=f"[bold bright_blue]2. M365 Workloads (Exchange / SharePoint / OneDrive / Teams / Intune / MDI)[/bold bright_blue]",
            border_style="bright_blue",
        )
    )

    eco_table = Table(box=box.ROUNDED, show_header=False, expand=True, border_style="magenta")
    eco_table.add_column("Intelligence Category", style="bold magenta", width=28)
    eco_table.add_column("Discovered Entities", style="white", overflow="fold")

    if res.co_tenant_domains:
        other_domains = [d for d in res.co_tenant_domains if d != res.domain]
        preview = ", ".join(other_domains[:60])
        if len(other_domains) > 60:
            preview += f" ... (+{len(other_domains) - 60} more)"
        eco_table.add_row(
            f"Co-Tenant / Sister Domains ({len(res.co_tenant_domains)})",
            f"[bold green]{preview or res.domain}[/bold green]",
        )
    else:
        eco_table.add_row("Co-Tenant / Sister Domains", "[dim]Autodiscover SOAP enumeration blocked or single-domain tenant[/dim]")

    eco_table.add_row(
        "Trusted Outbound Mail Partners (SPF)",
        ", ".join(f"[cyan]{p}[/cyan]" for p in res.spf_trusted_domains)
        if res.spf_trusted_domains
        else "[dim]None found in SPF[/dim]",
    )
    eco_table.add_row(
        "Verified 3rd-Party SaaS (DNS TXT)",
        ", ".join(f"[bold yellow]{s}[/bold yellow]" for s in res.verified_saas)
        if res.verified_saas
        else "[dim]No standard SaaS verification tokens matched in root TXT[/dim]",
    )

    console.print(
        Panel(
            eco_table,
            title=f"[bold magenta]3. Co-Tenant Sister Domains, Trusted Partners & SaaS Footprint[/bold magenta]",
            border_style="magenta",
        )
    )

    if not res.css_url:
        console.print(
            Panel(
                "[bold yellow]NO CUSTOM CSS BRANDING DETECTED[/bold yellow]\n"
                f"Banner Logo: {res.banner_logo_url or '[dim]None[/dim]'}\n"
                f"Illustration: {res.illustration_url or '[dim]None[/dim]'}\n"
                "[dim]Result: No CSS-based Canary token is currently active on this tenant.[/dim]",
                title="[bold yellow]4. Branding & Canary Token Status[/bold yellow]",
                border_style="yellow",
            )
        )
    elif not res.css_content:
        console.print(
            Panel(
                f"[bold yellow]CUSTOM CSS URL FOUND BUT EMPTY OR UNREACHABLE[/bold yellow]\n"
                f"CSS URL ({res.css_discovery_source}): [dim]{res.css_url}[/dim]",
                title="[bold yellow]4. Branding & Canary Token Status[/bold yellow]",
                border_style="yellow",
            )
        )
    elif res.has_canary:
        canary_table = Table(box=box.SIMPLE, show_header=False, expand=True)
        canary_table.add_column("Property", style="bold red", width=24)
        canary_table.add_column("Details", style="white", overflow="fold")

        canary_table.add_row("Canary Token In Use?", "[bold white on red] YES — ACTIVE ANTI-AITM BEACON [/bold white on red]")
        canary_table.add_row("Canary Provider", f"[bold yellow]{res.canary_provider}[/bold yellow]")
        canary_table.add_row("CSS Discovery Source", f"{res.css_discovery_source} -> {res.css_url}")
        canary_table.add_row("CSS Payload Size", f"{len(res.css_content)} bytes")
        canary_table.add_row(
            "Detected Beacon URL(s)",
            "\n".join(f"[bold red]• {u}[/bold red]" for u in res.canary_urls),
        )

        console.print(
            Panel(
                canary_table,
                title="[bold red]4. ⚠️  ANTI-AITM / CANARY TOKEN DETECTED[/bold red]",
                subtitle="[dim]Extracted statically — beacon URLs were NOT triggered during scan[/dim]",
                border_style="bright_red",
            )
        )
    elif res.needs_manual_review:
        console.print(
            Panel(
                f"[bold yellow]CUSTOM CSS PRESENT ({len(res.css_content)} bytes) — MANUAL REVIEW RECOMMENDED[/bold yellow]\n"
                f"CSS URL: [dim]{res.css_url}[/dim]\n"
                "Custom CSS branding was downloaded, but no external image URLs were matched.",
                title="[bold yellow]4. Custom CSS Found Without Standard Image Beacons[/bold yellow]",
                border_style="yellow",
            )
        )
        show_css = True
    else:
        clean_table = Table(box=box.SIMPLE, show_header=False, expand=True)
        clean_table.add_column("Property", style="bold green", width=24)
        clean_table.add_column("Details", style="white", overflow="fold")

        clean_table.add_row("Canary Token In Use?", "[bold green]No (Legitimate Microsoft / Data URIs only)[/bold green]")
        clean_table.add_row("CSS URL", res.css_url)
        clean_table.add_row("CSS Payload Size", f"{len(res.css_content)} bytes")
        clean_table.add_row(
            "Extracted CSS URLs",
            "\n".join(f"[dim]• {u}[/dim]" for u in res.all_extracted_urls) or "[dim]None[/dim]",
        )

        console.print(
            Panel(
                clean_table,
                title="[bold green]4. ✓ Custom CSS Present — No External Canary Beacons Detected[/bold green]",
                border_style="green",
            )
        )

    if show_css and res.css_content:
        console.print(
            Panel(
                Syntax(res.css_content, "css", theme="monokai", line_numbers=True, word_wrap=True),
                title=f"[bold magenta]Company Branding Custom CSS — {res.domain}[/bold magenta]",
                border_style="magenta",
            )
        )


def render_history_table(history: List[ScanResult]):
    if not history:
        console.print("[yellow]No domains scanned yet in this session.[/yellow]")
        return

    table = Table(
        title="M365 Deep Recon & Canary Summary",
        box=box.HEAVY_HEAD,
        border_style="bright_blue",
        expand=True,
    )
    table.add_column("#", style="dim", width=3)
    table.add_column("Domain", style="bold white")
    table.add_column("Tenant Slug / Region", style="cyan")
    table.add_column("Sister Domains", justify="right", style="magenta")
    table.add_column("MDI Sensor", justify="center")
    table.add_column("Custom CSS", justify="center")
    table.add_column("Canary In Use", justify="center")
    table.add_column("Provider / Notes", style="yellow", overflow="fold")

    for idx, item in enumerate(history, start=1):
        if item.error and not item.tenant_found and not item.css_url:
            table.add_row(str(idx), item.domain, "[red]Not Found[/red]", "-", "-", "-", "-", f"[red]{item.error}[/red]")
            continue

        slug_reg = f"{item.tenant_slug or '?'} ({item.tenant_region or '?'})"
        mdi_badge = "[bold red]YES[/bold red]" if item.mdi_sensor_detected else "[dim]No[/dim]"
        css_badge = "[green]Yes[/green]" if item.css_content else "[dim]No[/dim]"
        if item.has_canary:
            canary_badge = "[bold white on red] YES [/bold white on red]"
            provider_str = item.canary_provider or "Active Beacon"
        elif item.needs_manual_review:
            canary_badge = "[bold black on yellow] REVIEW [/bold black on yellow]"
            provider_str = "Custom CSS (no url())"
        else:
            canary_badge = "[green]No[/green]"
            provider_str = "[dim]None[/dim]"

        table.add_row(
            str(idx),
            item.domain,
            slug_reg,
            str(len(item.co_tenant_domains)),
            mdi_badge,
            css_badge,
            canary_badge,
            provider_str,
        )

    console.print()
    console.print(table)


def render_trigger_vector_inspector(res: ScanResult):
    console.print()
    if not res.css_content or not res.canary_urls:
        console.print(
            Panel(
                f"[yellow]No active Canary URLs were detected on [bold]{res.domain}[/bold] to analyze.[/yellow]",
                title="[bold yellow]Trigger Vector Inspector (Static Analysis)[/bold yellow]",
                border_style="yellow",
            )
        )
        return

    all_contexts: List[dict] = []
    selector_table = Table(
        title=f"CSS Selector & DOM Bindings — {res.domain}",
        box=box.ROUNDED,
        border_style="magenta",
        expand=True,
    )
    selector_table.add_column("Beacon URL", style="bold red", overflow="fold")
    selector_table.add_column("Bound CSS Selector", style="bold cyan")
    selector_table.add_column("CSS Property", style="green")
    selector_table.add_column("Browser DOM Loading Behavior", style="yellow", overflow="fold")

    for u in res.canary_urls:
        ctxs = extract_css_selector_context(res.css_content, u)
        all_contexts.extend(ctxs)
        for c in ctxs:
            selector_table.add_row(u, c["selector"], c["property"], c["dom_behavior"])

    console.print(selector_table)

    matrix = build_trigger_vector_matrix(res.canary_provider, all_contexts)
    matrix_table = Table(
        title=f"Static Trigger Vector Evaluation Matrix ({res.canary_provider or 'Detected Beacon'})",
        box=box.HEAVY_HEAD,
        border_style="bright_red",
        expand=True,
    )
    matrix_table.add_column("Trigger Vector", style="bold white", width=26)
    matrix_table.add_column("Simulated Request Profile", style="cyan", overflow="fold")
    matrix_table.add_column("Trigger Outcome", justify="center", width=22)
    matrix_table.add_column("Detection Mechanism", style="dim white", overflow="fold")

    for row in matrix:
        outcome = row["will_trigger"]
        if outcome.startswith("YES"):
            badge = f"[bold white on red] {outcome} [/bold white on red]"
        elif outcome.startswith("CONDITIONAL") or outcome.startswith("LIKELY"):
            badge = f"[bold black on yellow] {outcome} [/bold black on yellow]"
        else:
            badge = f"[dim]{outcome}[/dim]"
        matrix_table.add_row(row["vector"], row["header_profile"], badge, row["mechanism"])

    console.print()
    console.print(
        Panel(
            matrix_table,
            title="[bold bright_red]🔍 Canary Trigger Vector Analysis (Zero-Traffic Static Evaluation)[/bold bright_red]",
            border_style="bright_red",
        )
    )


def _build_headers_for_vector(preset: dict, base_headers: dict) -> dict:
    headers = dict(base_headers)
    headers.update(preset.get("headers", {}))
    if preset.get("remove_referer"):
        headers.pop("Referer", None)
    if preset.get("strip_sec_fetch"):
        for k in list(headers):
            if k.lower().startswith("sec-fetch-"):
                headers.pop(k, None)
    return headers


def _apply_http_downgrade(url: str) -> str:
    parsed = urlparse(url)
    if parsed.scheme == "https":
        return urlunparse(parsed._replace(scheme="http"))
    return url


def fire_trigger_vectors(
    urls: List[str],
    preset_keys: List[str],
    extra_header_overrides: Optional[dict] = None,
    knock_mode: bool = False,
) -> List[TriggerAttempt]:
    # If knock_mode, ensure preset "7" (Tool-Signature UA SIEM Knock) is included
    if knock_mode and "7" not in preset_keys:
        preset_keys = list(preset_keys) + ["7"]
    # If knock_mode, fire ALL urls in sequence
    if knock_mode:
        urls = list(urls)

    attempts: List[TriggerAttempt] = []
    for url in urls:
        for key in preset_keys:
            if key not in TRIGGER_VECTOR_PRESETS:
                continue
            preset = TRIGGER_VECTOR_PRESETS[key]
            target_url = _apply_http_downgrade(url) if preset.get("use_http") else url
            headers = _build_headers_for_vector(preset, HEADERS)
            if extra_header_overrides:
                for hk, hv in extra_header_overrides.items():
                    if hk == "Referer" and hv == "":
                        headers.pop("Referer", None)
                    else:
                        headers[hk] = hv

            attempt = TriggerAttempt(
                vector_name=preset["name"],
                url_requested=target_url,
                method="GET",
                request_headers=headers,
            )
            try:
                r = requests.get(
                    target_url,
                    headers=headers,
                    timeout=REQUEST_TIMEOUT,
                    allow_redirects=True,
                )
                attempt.status_code = r.status_code
                attempt.elapsed_ms = round(r.elapsed.total_seconds() * 1000.0, 1)
                ctype = r.headers.get("Content-Type", "unknown")
                attempt.response_snippet = f"{len(r.content)} bytes ({ctype})"
            except Timeout:
                attempt.error = "Timeout"
            except RequestException as exc:
                attempt.error = str(exc)
            except Exception as exc:
                attempt.error = f"Unexpected: {exc}"

            attempts.append(attempt)
    return attempts


def render_trigger_attempts(attempts: List[TriggerAttempt], domain: str):
    console.print()
    table = Table(
        title=f"Active Trigger Results — {domain}",
        box=box.HEAVY_HEAD,
        border_style="bright_red",
        expand=True,
    )
    table.add_column("Vector", style="bold white", width=28, overflow="fold")
    table.add_column("URL Requested", style="cyan", overflow="fold")
    table.add_column("Status", justify="center", width=8)
    table.add_column("Elapsed", justify="right", width=10, style="magenta")
    table.add_column("Response / Notes", style="dim white", overflow="fold")

    for a in attempts:
        elapsed_str = f"{a.elapsed_ms:.0f} ms" if a.elapsed_ms is not None else "-"
        if a.error:
            status_badge = "[bold white on red] ERR [/bold white on red]"
            detail = f"[red]{a.error}[/red]"
        else:
            status_badge = (
                f"[bold green]{a.status_code}[/bold green]"
                if a.status_code and a.status_code < 400
                else f"[bold yellow]{a.status_code}[/bold yellow]"
            )
            detail = a.response_snippet or ""
        table.add_row(a.vector_name, a.url_requested, status_badge, elapsed_str, detail)

    console.print(Panel(table, title="[bold bright_red]🔥 Beacons Fired[/bold bright_red]", border_style="bright_red"))

    sent_log = [
        {
            "vector": a.vector_name,
            "method": a.method,
            "url": a.url_requested,
            "status_code": a.status_code,
            "elapsed_ms": a.elapsed_ms,
            "headers_sent": a.request_headers,
        }
        for a in attempts
    ]
    console.print(
        Panel(
            Syntax(json.dumps(sent_log, indent=2), "json", theme="monokai", word_wrap=True),
            title="[bold cyan]Exact Request URLs & Headers Sent[/bold cyan]",
            border_style="cyan",
        )
    )


def run_knock_mode(
    profile: ReconProfile,
    urls: List[str],
    preset_keys: Optional[List[str]] = None,
    tenant_slug: Optional[str] = None,
    graph_token: Optional[str] = None,
) -> dict:
    """
    Execute Knock Mode ([K]):
      1. Fires ALL detected canary URLs in sequence with Preset 7 (TeamFiltration/3.5.0 UA) + selected vectors.
      2. Optional Entra SigninLogs Knock: Burst of GetCredentialType probes with TeamFiltration/3.5.0 UA.
      3. Optional SharePoint/OneDrive UAL Knock: Burst of 401 personal-site probes with tool-signature UA.
      4. Optional Graph Activity Log Knock: Rapid directory resource queries when a Bearer token is provided.
    """
    selected_keys = list(preset_keys) if preset_keys else list(TRIGGER_VECTOR_PRESETS.keys())
    canary_attempts: List[TriggerAttempt] = []
    if urls:
        canary_attempts = fire_trigger_vectors(
            urls,
            selected_keys,
            extra_header_overrides=None,
            knock_mode=True,
        )

    signin_knock_events = 0
    onedrive_knock_events = 0
    graph_knock_events = 0
    knock_ua = profile.tool_signature_ua or "TeamFiltration/3.5.0"
    clean_dom = profile.domain.strip().lower().split("@")[-1] if profile.domain else ""

    # 2. GetCredentialType Sign-in Log Burst + Tool-Signature UA Knock
    if profile.include_signin_knock and clean_dom and is_valid_domain(clean_dom):
        synthetic_Identities = [
            f"firstname.lastname.{i:02d}@{clean_dom}" if i % 2 == 0 else f"f.lastname.{i:02d}@{clean_dom}"
            for i in range(1, max(5, profile.burst_count) + 1)
        ]
        gct_headers = {
            "User-Agent": knock_ua,
            "Content-Type": "application/json; charset=UTF-8",
            "Accept": "application/json",
        }
        for u_email in synthetic_Identities:
            body = {
                "username": u_email,
                "isOtherIdpSupported": True,
                "checkPhones": False,
                "isRemoteNGCSupported": True,
                "isCookieBannerShown": False,
                "isFidoSupported": True,
                "originalRequest": "",
            }
            try:
                requests.post(
                    "https://login.microsoftonline.com/common/GetCredentialType",
                    json=body,
                    headers=gct_headers,
                    timeout=8,
                )
                signin_knock_events += 1
            except Exception:
                pass

    # 3. OneDrive / SharePoint Personal Site 401 Burst Knock
    if profile.include_onedrive_knock and clean_dom:
        if not tenant_slug and is_valid_domain(clean_dom):
            soap = get_federation_domains_soap(clean_dom)
            for d in soap.get("domains", []):
                if d.endswith(".onmicrosoft.com") and not d.endswith(".mail.onmicrosoft.com"):
                    tenant_slug = d[: -len(".onmicrosoft.com")]
                    break
        if tenant_slug:
            dom_underscored = clean_dom.replace(".", "_").replace("-", "_")
            common_prefixes = [
                "admin", "administrator", "john.smith", "j.smith", "jane.doe", "j.doe",
                "service.account", "helpdesk", "it.support", "security.ops",
                "finance.admin", "hr.portal", "ops.lead", "cloud.admin", "backup.svc",
                "audit.user", "compliance", "dev.ops", "soc.alert", "canary.knock",
            ]
            od_headers = {"User-Agent": knock_ua}
            for pref in common_prefixes[: max(5, min(50, profile.burst_count))]:
                safe_user = pref.replace(".", "_")
                od_url = f"https://{tenant_slug}-my.sharepoint.com/personal/{safe_user}_{dom_underscored}/_layouts/15/onedrive.aspx"
                try:
                    requests.head(od_url, headers=od_headers, timeout=6, allow_redirects=False)
                    onedrive_knock_events += 1
                except Exception:
                    pass

    # 4. Graph API Burst Knock (if Bearer token supplied)
    if profile.include_graph_knock and graph_token:
        g_headers = {
            "Authorization": f"Bearer {graph_token.strip()}",
            "User-Agent": knock_ua,
            "Accept": "application/json",
        }
        endpoints = ["users?$top=1", "groups?$top=1", "applications?$top=1", "servicePrincipals?$top=1", "directoryRoles"]
        for i in range(max(10, profile.burst_count)):
            ep = endpoints[i % len(endpoints)]
            try:
                requests.get(f"https://graph.microsoft.com/v1.0/{ep}", headers=g_headers, timeout=6)
                graph_knock_events += 1
            except Exception:
                pass

    return {
        "domain": profile.domain or "custom-target",
        "canary_attempts": canary_attempts,
        "tool_ua": knock_ua,
        "signin_knock_events": signin_knock_events,
        "onedrive_knock_events": onedrive_knock_events,
        "graph_knock_events": graph_knock_events,
    }


def render_knock_summary(knock_res: dict):
    domain = knock_res.get("domain") or "target"
    attempts: List[TriggerAttempt] = knock_res.get("canary_attempts") or []
    if attempts:
        render_trigger_attempts(attempts, domain)

    summary_table = Table(
        title=f"🔔 Knock Mode Telemetry Summary — {domain}",
        box=box.HEAVY_HEAD,
        border_style="bright_red",
        expand=True,
    )
    summary_table.add_column("Knock Vector", style="bold white", width=34)
    summary_table.add_column("Volume Sent", justify="right", width=14, style="bold yellow")
    summary_table.add_column("Expected SIEM / SOC Alert Rule Triggered", style="cyan", overflow="fold")

    summary_table.add_row(
        "1. Canary Beacon Trigger (All URLs)",
        f"{len(attempts)} reqs",
        "Thinkst Canary / CSS Webhook Alert + Foreign Referer + Scheme Downgrade",
    )
    summary_table.add_row(
        "2. Tool-Signature UA (Preset 7)",
        f"UA: {knock_res.get('tool_ua')}",
        "Elastic / Sentinel Rule: 'Entra ID Sign-in TeamFiltration User-Agent Detected' (user_agent.original)",
    )
    summary_table.add_row(
        "3. GetCredentialType Sign-in Burst",
        f"{knock_res.get('signin_knock_events', 0)} probes",
        "Entra SigninLogs Volume Alert: Rapid GetCredentialType enumeration cluster from single IP",
    )
    summary_table.add_row(
        "4. OneDrive / SPO 401 Access Burst",
        f"{knock_res.get('onedrive_knock_events', 0)} probes",
        "SharePoint Unified Audit Log (UAL) / Sentinel: Anomalous 401 access attempts to personal sites",
    )
    summary_table.add_row(
        "5. Graph API Activity Burst",
        f"{knock_res.get('graph_knock_events', 0)} calls",
        "Graph Activity Logs: High-frequency /users, /groups, /applications directory enumeration",
    )

    console.print()
    console.print(
        Panel(
            summary_table,
            title="[bold white on red] 🔔 KNOCK MODE EXECUTED — MAXIMUM DETECTION LIKELIHOOD [/bold white on red]",
            subtitle="[bold yellow]Expect SOC alerts: Canary trigger, UA-based detection, burst pattern.[/bold yellow]",
            border_style="bright_red",
        )
    )
    console.print("[bold red]Expect SOC alerts: Canary trigger, UA-based detection, burst pattern.[/bold red]")


def interactive_knock_menu(
    last_result: Optional[ScanResult], trigger_history: List[TriggerAttempt]
) -> tuple:
    console.print()
    console.print(
        Panel(
            "[bold red]🔔 KNOCK MODE — DELIBERATE SIEM & CANARY ALERT GENERATION[/bold red]\n"
            "Fires all detected Canary URLs in sequence using Preset [7] (Tool-Signature UA: TeamFiltration/3.5.0) "
            "plus selected trigger vectors, and optionally generates Entra SigninLogs / SharePoint 401 / Graph burst patterns.",
            title="[bold bright_red][K] Knock Mode[/bold bright_red]",
            border_style="bright_red",
        )
    )

    if not Confirm.ask("[bold yellow]Confirm authorization to execute Knock Mode against target?[/bold yellow]", default=True):
        console.print("[dim]Knock Mode cancelled.[/dim]")
        return trigger_history, None

    detected_urls = list(last_result.canary_urls) if last_result and last_result.canary_urls else []
    target_domain = last_result.domain if last_result else ""

    if not detected_urls:
        raw_target = Prompt.ask(
            "[bold green]Enter target M365 domain or custom Canary URL(s) (comma-separated)[/bold green]",
            default=target_domain,
        ).strip()
        if not raw_target:
            return trigger_history, None
        if is_valid_domain(raw_target):
            res = run_scan(raw_target)
            target_domain = res.domain
            detected_urls = list(res.canary_urls)
        else:
            detected_urls = [u.strip() for u in raw_target.split(",") if u.strip()]
            target_domain = urlparse(detected_urls[0]).netloc or "custom-target"

    vec_sel = Prompt.ask(
        "Vectors to fire alongside Preset [7] ([dim]'a' = all 7 vectors, or comma-separated keys[/dim])",
        default="a",
    ).strip().lower()
    if vec_sel in ("a", "all"):
        preset_keys = list(TRIGGER_VECTOR_PRESETS.keys())
    else:
        preset_keys = [k.strip() for k in vec_sel.split(",") if k.strip() in TRIGGER_VECTOR_PRESETS]
        if "7" not in preset_keys:
            preset_keys.append("7")

    inc_signin = Confirm.ask(
        "Also fire GetCredentialType Sign-in Log burst (25 probes with TeamFiltration/3.5.0 UA)?",
        default=True if (target_domain and is_valid_domain(target_domain)) else False,
    )
    inc_onedrive = Confirm.ask(
        "Also fire OneDrive/SharePoint 401 personal-site burst (SharePoint UAL knock)?",
        default=True if (target_domain and is_valid_domain(target_domain)) else False,
    )
    graph_tok = Prompt.ask(
        "Optional Graph Bearer Token for Graph API burst knock ([dim]Enter to skip[/dim])",
        default="",
    ).strip()

    profile = ReconProfile(
        domain=target_domain,
        knock_mode=True,
        tool_signature_ua="TeamFiltration/3.5.0",
        include_signin_knock=inc_signin,
        include_onedrive_knock=inc_onedrive,
        include_graph_knock=bool(graph_tok),
        burst_count=25,
    )

    with console.status("[bold red]🔔 Executing Knock Mode (Canary + Tool-Signature UA + SIEM Bursts)...", spinner="bouncingBar"):
        knock_res = run_knock_mode(
            profile=profile,
            urls=detected_urls,
            preset_keys=preset_keys,
            tenant_slug=last_result.tenant_slug if last_result else None,
            graph_token=graph_tok or None,
        )

    attempts = knock_res.get("canary_attempts") or []
    trigger_history.extend(attempts)
    render_knock_summary(knock_res)

    ctx = None
    if detected_urls:
        ctx = {
            "domain": target_domain or "custom-target",
            "urls": detected_urls,
            "css_url": last_result.css_url if last_result else None,
            "vectors": preset_keys,
            "headers": {},
            "captured_at": datetime.now(timezone.utc).isoformat(),
        }
    return trigger_history, ctx


def interactive_trigger_menu(
    last_result: Optional[ScanResult], trigger_history: List[TriggerAttempt]
) -> tuple:
    detected_urls = list(last_result.canary_urls) if last_result and last_result.canary_urls else []
    target_label = last_result.domain if last_result else "custom-target"

    console.print()
    if detected_urls:
        console.print("[bold cyan]Target Beacon URL(s):[/bold cyan]")
        for i, u in enumerate(detected_urls, start=1):
            console.print(f"  [bold cyan][{i}][/bold cyan] {u}")
        console.print("  [bold cyan][c][/bold cyan] Enter a custom URL")
        console.print("  [bold cyan][a][/bold cyan] All detected URLs")

        raw_sel = Prompt.ask("Select URL (index, 'a' for all, or 'c' for custom)", default="1").strip()
        if raw_sel.lower() in ("c", "custom"):
            custom_u = Prompt.ask("[bold green]Enter custom Canary URL[/bold green]").strip()
            if not custom_u:
                return trigger_history, None
            selected_urls = [custom_u]
            target_label = urlparse(custom_u).netloc or "custom-url"
        elif raw_sel.lower() in ("a", "all"):
            selected_urls = detected_urls
        elif raw_sel.startswith("http://") or raw_sel.startswith("https://"):
            selected_urls = [raw_sel]
            target_label = urlparse(raw_sel).netloc or "custom-url"
        else:
            selected_urls = []
            for token in raw_sel.split(","):
                token = token.strip()
                if token.isdigit() and 1 <= int(token) <= len(detected_urls):
                    selected_urls.append(detected_urls[int(token) - 1])
            if not selected_urls:
                console.print("[yellow]No valid URL selected.[/yellow]")
                return trigger_history, None
    else:
        custom_u = Prompt.ask(
            "[bold green]No canary URLs from last scan — enter a custom Canary URL to trigger[/bold green]"
        ).strip()
        if not custom_u:
            return trigger_history, None
        selected_urls = [custom_u]
        target_label = urlparse(custom_u).netloc or "custom-url"

    console.print("\n[bold cyan]Trigger Vectors:[/bold cyan]")
    for k, preset in TRIGGER_VECTOR_PRESETS.items():
        console.print(f"  [bold cyan][{k}][/bold cyan] {preset['name']} — [dim]{preset['description']}[/dim]")
    console.print("  [bold cyan][a][/bold cyan] All 7 vectors")

    vec_sel = Prompt.ask("Select vector(s) (e.g. 1, 1,4,7, or 'a' for all)", default="1").strip().lower()
    if vec_sel in ("a", "all"):
        preset_keys = list(TRIGGER_VECTOR_PRESETS.keys())
    else:
        preset_keys = [t.strip() for t in vec_sel.split(",") if t.strip() in TRIGGER_VECTOR_PRESETS]
    if not preset_keys:
        console.print("[yellow]No valid vector keys selected.[/yellow]")
        return trigger_history, None

    custom_ref = Prompt.ask(
        "Optional custom Referer override ([dim]Enter = keep vector defaults, 'none' = strip Referer[/dim])",
        default="",
    ).strip()
    extra_headers: dict = {}
    if custom_ref.lower() == "none":
        extra_headers["Referer"] = ""
    elif custom_ref:
        extra_headers["Referer"] = custom_ref

    with console.status("[bold red]Firing canary beacons...", spinner="bouncingBar"):
        attempts = fire_trigger_vectors(
            selected_urls,
            preset_keys,
            extra_header_overrides=extra_headers if extra_headers else None,
        )

    trigger_history.extend(attempts)
    render_trigger_attempts(attempts, target_label)
    return trigger_history, {
        "domain": target_label,
        "urls": selected_urls,
        "css_url": last_result.css_url if last_result else None,
        "vectors": preset_keys,
        "headers": extra_headers or {},
        "captured_at": datetime.now(timezone.utc).isoformat(),
    }


def _randomize_urls(urls: List[str], count: int = 20) -> List[str]:
    pool: List[str] = []
    nonce_alphabet = "abcdefghijklmnopqrstuvwxyz0123456789"
    for u in urls:
        for _ in range(max(1, count // max(1, len(urls)))):
            nonce = "".join(random.choices(nonce_alphabet, k=10))
            parsed = urlparse(u)
            new_query = f"{parsed.query}&cb={nonce}" if parsed.query else f"cb={nonce}"
            scheme = "http" if random.random() < 0.15 else parsed.scheme
            new_url = urlunparse(parsed._replace(scheme=scheme, query=new_query))
            pool.append(new_url)
    random.shuffle(pool)
    return pool


def create_recurring_task(
    ctx: dict,
    task_name: str,
    work_dir: Path,
    urls_pool: List[str],
    interval_minutes: int = 10,
    auto_heal: bool = True,
    randomize_pool: bool = True,
) -> dict:
    work_dir.mkdir(parents=True, exist_ok=True)
    state_path = work_dir / "m365_canary_recurring.json"
    ps1_path = work_dir / "m365_canary_recurring.ps1"
    bat_path = work_dir / "run_recurring.bat"
    log_path = work_dir / "m365_canary_recurring.log"

    state = {
        "domain": ctx["domain"],
        "css_url": ctx.get("css_url"),
        "base_urls": sorted(set(ctx["urls"])),
        "urls": urls_pool,
        "vectors": ctx["vectors"],
        "overrides": ctx.get("headers") or {},
        "auto_heal": auto_heal,
        "randomize_pool": randomize_pool,
        "heal_count": 0,
        "last_healed_at": None,
        "captured_at": ctx.get("captured_at") or datetime.now(timezone.utc).isoformat(),
        "interval_minutes": interval_minutes,
        "generator": "m365_canary_tui menu [9] (self-healing)",
    }
    state_path.write_text(json.dumps(state, indent=2), encoding="utf-8")
    ps1_path.write_text(POWERSHELL_WORKER_TEMPLATE.lstrip("\n"), encoding="utf-8")

    bat_contents = (
        "@echo off\r\n"
        "powershell.exe -NoProfile -ExecutionPolicy Bypass "
        f'-File "%~dp0{ps1_path.name}" '
        f'-StatePath "%~dp0{state_path.name}" '
        f'-LogPath "%~dp0{log_path.name}"\r\n'
    )
    bat_path.write_text(bat_contents, encoding="utf-8")

    cmd = [
        "schtasks", "/Create", "/F",
        "/TN", task_name,
        "/SC", "MINUTE",
        "/MO", str(interval_minutes),
        "/TR", f'"{bat_path}"',
        "/RL", "LIMITED",
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True, shell=False)
    return {
        "task_name": task_name,
        "state_path": str(state_path),
        "ps1_path": str(ps1_path),
        "bat_path": str(bat_path),
        "log_path": str(log_path),
        "schtasks_rc": proc.returncode,
        "schtasks_stdout": proc.stdout.strip(),
        "schtasks_stderr": proc.stderr.strip(),
    }


def interactive_recurring_menu(ctx: Optional[dict], last_result: Optional[ScanResult] = None) -> None:
    if platform.system().lower() != "windows":
        console.print("[yellow]Windows Task Scheduler is only supported on Windows.[/yellow]")
        return

    if not ctx or not ctx.get("urls"):
        if last_result and last_result.canary_urls:
            ctx = {
                "domain": last_result.domain,
                "css_url": last_result.css_url,
                "urls": list(last_result.canary_urls),
                "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                "headers": {},
                "captured_at": datetime.now(timezone.utc).isoformat(),
            }
        else:
            custom_dom = Prompt.ask(
                "[bold green]Enter target M365 domain (or Canary URL)[/bold green]",
                default="",
            ).strip()
            if not custom_dom:
                return
            if is_valid_domain(custom_dom):
                scan_res = run_scan(custom_dom)
                if scan_res.canary_urls:
                    ctx = {
                        "domain": scan_res.domain,
                        "css_url": scan_res.css_url,
                        "urls": list(scan_res.canary_urls),
                        "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                        "headers": {},
                        "captured_at": datetime.now(timezone.utc).isoformat(),
                    }
                else:
                    custom_u = Prompt.ask(
                        "[bold green]No Canary URL auto-detected on domain — enter initial Canary URL[/bold green]"
                    ).strip()
                    if not custom_u:
                        return
                    urls_input = [u.strip() for u in custom_u.split(",") if u.strip()]
                    ctx = {
                        "domain": custom_dom,
                        "css_url": scan_res.css_url,
                        "urls": urls_input,
                        "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                        "headers": {},
                        "captured_at": datetime.now(timezone.utc).isoformat(),
                    }
            else:
                urls_input = [u.strip() for u in custom_dom.split(",") if u.strip()]
                ctx = {
                    "domain": urlparse(urls_input[0]).netloc or "custom-target",
                    "css_url": None,
                    "urls": urls_input,
                    "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                    "headers": {},
                    "captured_at": datetime.now(timezone.utc).isoformat(),
                }

    console.print()
    default_task = f"M365CanaryRecurring_{re.sub(r'[^A-Za-z0-9_-]', '_', ctx['domain'])}"
    task_name = Prompt.ask("[bold green]Windows task name[/bold green]", default=default_task).strip()
    if not task_name:
        return

    default_dir = str(Path.home() / "M365CanaryTasks" / task_name)
    work_dir_str = Prompt.ask("[bold green]Work directory[/bold green]", default=default_dir).strip()
    work_dir = Path(work_dir_str).expanduser()

    auto_heal = Confirm.ask(
        "Enable Auto-Heal (re-polls live M365 Custom CSS each run & updates token pool if rotated)?",
        default=True,
    )

    use_rand = Confirm.ask(
        "Auto-generate randomized URL pool (cache-buster queries + ~15% HTTP-downgrade)?",
        default=True,
    )
    if use_rand:
        urls_pool = _randomize_urls(ctx["urls"], count=max(20, 5 * len(ctx["urls"])))
        console.print(f"[dim]Generated {len(urls_pool)} randomized URL variants.[/dim]")
    else:
        raw = Prompt.ask("Paste comma-separated URLs (Enter = use detected URLs as-is)", default="").strip()
        urls_pool = [u.strip() for u in re.split(r"[,\r\n]+", raw) if u.strip()] if raw else list(ctx["urls"])

    interval_raw = Prompt.ask("[bold green]Interval in minutes[/bold green]", default="10").strip()
    interval = int(interval_raw) if interval_raw.isdigit() and int(interval_raw) > 0 else 10

    info = create_recurring_task(
        ctx,
        task_name,
        work_dir,
        urls_pool,
        interval_minutes=interval,
        auto_heal=auto_heal,
        randomize_pool=use_rand,
    )
    if info["schtasks_rc"] != 0:
        console.print(
            Panel(
                f"[bold red]schtasks failed (rc={info['schtasks_rc']})[/bold red]\n"
                f"stderr: {info['schtasks_stderr'] or info['schtasks_stdout']}",
                title="[bold red]Task Registration Failed[/bold red]",
                border_style="red",
            )
        )
        return

    console.print(
        Panel(
            f"[bold green]✓ Self-Healing Scheduled Task Installed[/bold green]\n\n"
            f"Task name : [cyan]{info['task_name']}[/cyan]\n"
            f"Auto-Heal : [bold green]{'ENABLED (auto-updates JSON when tenant rotates CSS token)' if auto_heal else 'Disabled'}[/bold green]\n"
            f"State file: [cyan]{info['state_path']}[/cyan]\n"
            f"PS1 worker: [cyan]{info['ps1_path']}[/cyan]\n"
            f"BAT shim  : [cyan]{info['bat_path']}[/cyan]\n"
            f"Log file  : [cyan]{info['log_path']}[/cyan]\n\n"
            f'[dim]Verify :[/dim]  schtasks /Query /TN "{info["task_name"]}" /V /FO LIST\n'
            f'[dim]Run now:[/dim]  schtasks /Run   /TN "{info["task_name"]}"\n'
            f'[dim]Remove :[/dim]  schtasks /Delete /TN "{info["task_name"]}" /F',
            title="[bold green]Recurring Trigger Task — Installed[/bold green]",
            border_style="green",
        )
    )


def heal_task_state_now(task_dir: Path) -> dict:
    """
    Upgrade a scheduled task's PowerShell worker to the latest self-healing template,
    poll the live M365 tenant CSS for token changes, and auto-heal m365_canary_recurring.json.
    """
    state_path = task_dir / "m365_canary_recurring.json"
    ps1_path = task_dir / "m365_canary_recurring.ps1"
    log_path = task_dir / "m365_canary_recurring.log"

    if not state_path.exists():
        return {"ok": False, "error": f"State file not found: {state_path}"}

    # Always upgrade worker script on disk so older tasks gain self-healing
    ps1_path.write_text(POWERSHELL_WORKER_TEMPLATE.lstrip("\n"), encoding="utf-8")

    state = json.loads(state_path.read_text(encoding="utf-8"))
    domain = state.get("domain") or ""
    old_base = sorted(set(state.get("base_urls") or state.get("urls") or []))
    now_iso = datetime.now(timezone.utc).isoformat()
    state["last_checked_at"] = now_iso

    # Recover domain if user previously typed a bare domain into the URL field
    if domain in ("", "custom-target", "custom-url") and old_base:
        candidate = re.sub(r"^https?:/*", "", old_base[0]).split("?")[0].split("/")[0].strip()
        if is_valid_domain(candidate):
            domain = candidate
            state["domain"] = domain

    live_urls: List[str] = []
    live_css_url: Optional[str] = state.get("css_url")

    if domain and domain not in ("custom-target", "custom-url") and is_valid_domain(domain):
        whr_resp = make_request(f"{BASE_URL}/?whr={domain}&_ts={int(datetime.now(timezone.utc).timestamp())}")
        if whr_resp:
            extracted_css = extract_custom_css_url_from_whr(whr_resp.text)
            if extracted_css:
                live_css_url = extracted_css

        if not live_css_url:
            cred = get_credential_type(f"canarycheck@{domain}")
            if "error" not in cred:
                b = extract_branding_from_cred(cred)
                if b.get("css_url"):
                    live_css_url = b["css_url"]

    if live_css_url:
        sep = "&" if "?" in live_css_url else "?"
        css_resp = make_request(f"{live_css_url}{sep}_cb={int(datetime.now(timezone.utc).timestamp())}")
        if css_resp and css_resp.text.strip():
            analysis = analyse_css(css_resp.text)
            live_urls = sorted(set(analysis.get("canary_urls") or []))

    rotated = False
    if live_urls and live_urls != old_base:
        rotated = True
        use_rand = state.get("randomize_pool", True)
        new_pool = _randomize_urls(live_urls, count=max(20, 5 * len(live_urls))) if use_rand else list(live_urls)
        state["previous_base_urls"] = old_base
        state["base_urls"] = live_urls
        state["urls"] = new_pool
        state["css_url"] = live_css_url
        state["last_healed_at"] = now_iso
        state["heal_count"] = int(state.get("heal_count") or 0) + 1
        state["auto_heal"] = True
        log_line = (
            f"{now_iso} [MANUAL-AUTO-HEAL #{state['heal_count']}] Token rotation detected for {domain}! "
            f"Old=({'|'.join(old_base)}) -> New=({'|'.join(live_urls)}). Regenerated {len(new_pool)} pool URLs.\n"
        )
        with open(log_path, "a", encoding="utf-8") as lf:
            lf.write(log_line)
    else:
        if "base_urls" not in state:
            state["base_urls"] = old_base
        state["auto_heal"] = True

    state_path.write_text(json.dumps(state, indent=2), encoding="utf-8")
    return {
        "ok": True,
        "rotated": rotated,
        "domain": domain,
        "old_base": old_base,
        "new_base": live_urls or old_base,
        "heal_count": state.get("heal_count", 0),
        "pool_size": len(state.get("urls") or []),
    }


def _query_schtasks_status(task_name: str) -> str:
    if platform.system().lower() != "windows":
        return "N/A (Non-Windows)"
    try:
        proc = subprocess.run(
            ["schtasks", "/Query", "/TN", task_name, "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            shell=False,
            timeout=6,
        )
        if proc.returncode == 0 and proc.stdout.strip():
            parts = [p.strip('"') for p in proc.stdout.strip().splitlines()[0].split('","')]
            if len(parts) >= 3:
                return f"[bold green]{parts[2]}[/bold green] (Next: {parts[1]})"
            return "[bold green]Registered[/bold green]"
    except Exception:
        pass
    return "[dim yellow]Not in Scheduler[/dim yellow]"



# ---------------------------------------------------------------------------
# Output Mode Aware Renderers & HTML Report Exporter
# ---------------------------------------------------------------------------

def _render_output_mode(data, default_rich_fn):
    """Dispatch rendering based on current profile output_mode."""
    mode = getattr(_CURRENT_PROFILE, "output_mode", "verbose")
    if mode == "json":
        console.print(json.dumps(data, indent=2, default=str))
    elif mode == "csv":
        import io
        import csv
        output = io.StringIO()
        writer = csv.writer(output)
        writer.writerow(["Probe", "Field", "Value"])
        if isinstance(data, dict):
            for k, v in data.items():
                writer.writerow(["assessment", k, str(v)])
        elif isinstance(data, list):
            for idx, item in enumerate(data):
                if isinstance(item, dict):
                    for k, v in item.items():
                        writer.writerow([f"row_{idx}", k, str(v)])
                else:
                    writer.writerow([f"row_{idx}", "value", str(item)])
        console.print(output.getvalue())
    elif mode == "table":
        t = Table(box=box.SIMPLE, expand=True)
        t.add_column("Field", style="bold cyan")
        t.add_column("Value", style="white")
        if isinstance(data, dict):
            for k, v in data.items():
                t.add_row(str(k), str(v))
        elif isinstance(data, list):
            for i, row in enumerate(data):
                t.add_row(f"Item #{i+1}", str(row))
        console.print(t)
    else:
        default_rich_fn()


def render_recon_panels(results: dict):
    """Render external tenant profile and recon results in configured output mode."""
    def _draw():
        t = Table(title="Tenant Posture Review Findings", box=box.ROUNDED, expand=True, border_style="cyan")
        t.add_column("Property", style="bold cyan", width=28)
        t.add_column("Value", style="white", overflow="fold")
        for k, v in results.items():
            if k == "_detection_risk":
                continue
            t.add_row(str(k), str(v))
        console.print(Panel(t, title="[bold cyan]External Tenant Profile Review[/bold cyan]", border_style="cyan"))
        risk = results.get("_detection_risk")
        if risk:
            console.print(f"[dim]Detection Risk: {risk.get('risk')} | Audit Logs: {risk.get('audit_logs')} | {risk.get('notes')}[/dim]")
    _render_output_mode(results, _draw)


def render_internal_results(results: dict, title: str = "Internal Posture Audit"):
    """Render internal posture review findings respecting output_mode."""
    def _draw():
        t = Table(title=title, box=box.HEAVY_HEAD, expand=True, border_style="green")
        t.add_column("Audit Metric / Property", style="bold green", width=30)
        t.add_column("Finding / Telemetry", style="white", overflow="fold")
        for k, v in results.items():
            if k == "_detection_risk":
                continue
            if isinstance(v, list):
                val_str = f"{len(v)} item(s) logged" if len(v) > 5 else "\n".join(str(x) for x in v)
            else:
                val_str = str(v)
            t.add_row(str(k), val_str)
        console.print(Panel(t, title=f"[bold green]🛡️ {title}[/bold green]", border_style="green"))
        risk = results.get("_detection_risk")
        if risk:
            console.print(f"[dim]Audit Reference: Risk={risk.get('risk')} | Audit Logs={risk.get('audit_logs')} | {risk.get('notes')}[/dim]")
    _render_output_mode(results, _draw)


def export_html_report(all_results: dict, output_path: str):
    """Generate self-contained HTML assessment report with risk annotations."""
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    
    sections_html = []
    for section_title, data in all_results.items():
        risk = data.get("_detection_risk", {}) if isinstance(data, dict) else {}
        risk_badge = f"<span class='badge risk-{risk.get('risk', 'low').lower().replace(' ', '-')}'>{risk.get('risk', 'Standard')}</span>" if risk else ""
        
        rows = []
        if isinstance(data, dict):
            for k, v in data.items():
                if k == "_detection_risk":
                    continue
                rows.append(f"<tr><th>{k}</th><td><pre>{json.dumps(v, indent=2) if isinstance(v, (dict, list)) else v}</pre></td></tr>")
        elif isinstance(data, list):
            for idx, item in enumerate(data):
                rows.append(f"<tr><th>Item #{idx+1}</th><td><pre>{json.dumps(item, indent=2) if isinstance(item, (dict, list)) else item}</pre></td></tr>")
        
        table_html = f"<table>{''.join(rows)}</table>" if rows else "<p>No data recorded.</p>"
        notes = f"<div class='risk-notes'><strong>Audit Telemetry:</strong> {risk.get('audit_logs', 'None')} &mdash; {risk.get('notes', '')}</div>" if risk else ""
        
        sections_html.append(f"""
        <div class="card">
            <h2>{section_title} {risk_badge}</h2>
            {notes}
            {table_html}
        </div>
        """)

    html = f"""<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>M365 Tenant & Posture Assessment Report</title>
<style>
body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 24px; }}
.container {{ max-width: 1200px; margin: 0 auto; }}
h1 {{ color: #38bdf8; font-size: 24px; border-bottom: 2px solid #1e293b; padding-bottom: 12px; margin-bottom: 24px; }}
.meta {{ color: #94a3b8; font-size: 13px; margin-bottom: 24px; }}
.card {{ background: #1e293b; border-radius: 8px; padding: 20px; margin-bottom: 20px; border: 1px solid #334155; }}
.card h2 {{ color: #f1f5f9; font-size: 18px; margin-top: 0; margin-bottom: 12px; display: flex; justify-content: space-between; align-items: center; }}
table {{ width: 100%; border-collapse: collapse; margin-top: 12px; font-size: 13px; }}
th, td {{ padding: 10px 12px; text-align: left; border-bottom: 1px solid #334155; }}
th {{ width: 28%; color: #38bdf8; background: #0f172a; }}
pre {{ margin: 0; white-space: pre-wrap; font-family: "Cascadia Code", Consolas, monospace; font-size: 12px; }}
.badge {{ font-size: 11px; padding: 4px 8px; border-radius: 4px; font-weight: bold; text-transform: uppercase; }}
.risk-none {{ background: #065f46; color: #34d399; }}
.risk-very-low {{ background: #065f46; color: #a7f3d0; }}
.risk-low {{ background: #854d0e; color: #fef08a; }}
.risk-medium {{ background: #9a3412; color: #fed7aa; }}
.risk-high {{ background: #991b1b; color: #fecaca; }}
.risk-notes {{ font-size: 12px; color: #94a3b8; background: #0f172a; padding: 8px 12px; border-radius: 4px; margin-bottom: 12px; }}
</style>
</head>
<body>
<div class="container">
    <h1>🛡️ M365 Security & Internal Posture Assessment Report</h1>
    <div class="meta">Engagement Report &bull; Generated: {ts} &bull; Output Mode: {_CURRENT_PROFILE.output_mode}</div>
    {''.join(sections_html)}
</div>
</body>
</html>"""

    out.write_text(html, encoding="utf-8")
    console.print(Panel(f"[bold green]✓ Assessment HTML Report Exported:[/bold green] [cyan]{out.resolve()}[/cyan]", title="HTML Report Generated", border_style="green"))


def interactive_profile_manager():
    """Menu [P]: Profile Manager to create, load, edit, and delete named assessment profiles."""
    global _CURRENT_PROFILE
    console.print()
    console.print(Rule("[bold magenta]Recon Profile Manager[/bold magenta]", style="magenta"))
    console.print(f"Current Profile: [bold cyan]{_CURRENT_PROFILE.name}[/bold cyan] | Output Mode: [yellow]{_CURRENT_PROFILE.output_mode}[/yellow] | Delay: {_CURRENT_PROFILE.inter_request_delay_ms}ms")
    
    profiles = list_profiles()
    console.print(f"Saved Profiles: {', '.join(profiles) if profiles else '[dim]None[/dim]'}")
    console.print()
    console.print("  [bold cyan][1][/bold cyan] Load an existing profile")
    console.print("  [bold cyan][2][/bold cyan] Create / save current profile")
    console.print("  [bold cyan][3][/bold cyan] Edit profile settings (output mode, delays, probe toggles)")
    console.print("  [bold cyan][4][/bold cyan] Delete a profile")
    console.print("  [bold cyan][5][/bold cyan] Return to main menu")
    
    action = Prompt.ask("Choose profile action", choices=["1", "2", "3", "4", "5"], default="5")
    if action == "1":
        if not profiles:
            console.print("[yellow]No saved profiles found.[/yellow]")
            return
        name = Prompt.ask("Profile name to load", choices=profiles, default=profiles[0])
        loaded = load_profile(name)
        if loaded:
            _CURRENT_PROFILE = loaded
            console.print(f"[bold green]✓ Loaded profile '{name}'[/bold green]")
    elif action == "2":
        name = Prompt.ask("Enter name to save current profile as", default=_CURRENT_PROFILE.name or "engagement").strip()
        _CURRENT_PROFILE.name = name
        saved_path = save_profile(_CURRENT_PROFILE)
        console.print(f"[bold green]✓ Saved profile '{name}' to {saved_path}[/bold green]")
    elif action == "3":
        mode = Prompt.ask("Select output mode", choices=["verbose", "json", "csv", "table"], default=_CURRENT_PROFILE.output_mode)
        _CURRENT_PROFILE.output_mode = mode
        delay = Prompt.ask("Inter-request delay (ms)", default=str(_CURRENT_PROFILE.inter_request_delay_ms))
        if delay.isdigit():
            _CURRENT_PROFILE.inter_request_delay_ms = int(delay)
        max_u = Prompt.ask("Max users to enumerate", default=str(_CURRENT_PROFILE.max_users_to_enum))
        if max_u.isdigit():
            _CURRENT_PROFILE.max_users_to_enum = int(max_u)
        raw_res = Confirm.ask("Include raw responses in output?", default=_CURRENT_PROFILE.include_raw_responses)
        _CURRENT_PROFILE.include_raw_responses = raw_res
        save_profile(_CURRENT_PROFILE)
        console.print("[bold green]✓ Profile updated and persisted.[/bold green]")
    elif action == "4":
        if not profiles:
            console.print("[yellow]No saved profiles to delete.[/yellow]")
            return
        name = Prompt.ask("Profile name to delete", choices=profiles)
        if Confirm.ask(f"Are you sure you want to delete profile '{name}'?", default=False):
            delete_profile(name)
            console.print(f"[bold green]✓ Profile '{name}' deleted.[/bold green]")


def interactive_manage_tasks_menu() -> None:
    """Menu [8]: Inspect, Force Auto-Heal, Run Now, View Logs, or Delete Scheduled Tasks."""
    tasks_root = Path.home() / "M365CanaryTasks"
    tasks_root.mkdir(parents=True, exist_ok=True)

    while True:
        task_dirs = sorted(
            [d for d in tasks_root.iterdir() if d.is_dir() and (d / "m365_canary_recurring.json").exists()]
        )
        console.print()
        if not task_dirs:
            console.print(
                Panel(
                    f"[yellow]No recurring tasks found in [bold]{tasks_root}[/bold].[/yellow]\n"
                    "[dim]Use option [9] to create a self-healing recurring scheduled task first.[/dim]",
                    title="[bold cyan]8. Scheduled Task & Auto-Heal Manager[/bold cyan]",
                    border_style="cyan",
                )
            )
            return

        table = Table(
            title=f"Installed M365 Canary Scheduled Tasks ({tasks_root})",
            box=box.HEAVY_HEAD,
            border_style="cyan",
            expand=True,
        )
        table.add_column("#", style="bold cyan", width=3)
        table.add_column("Task Name", style="bold white", overflow="fold")
        table.add_column("Domain", style="green")
        table.add_column("Scheduler Status", overflow="fold")
        table.add_column("Auto-Heal", justify="center")
        table.add_column("Heals", justify="right", style="magenta")
        table.add_column("Active Base Canary URL(s)", style="yellow", overflow="fold")
        table.add_column("Last Tick Log", style="dim white", overflow="fold")

        states_cache: List[dict] = []
        for idx, tdir in enumerate(task_dirs, start=1):
            st_path = tdir / "m365_canary_recurring.json"
            lg_path = tdir / "m365_canary_recurring.log"
            try:
                st = json.loads(st_path.read_text(encoding="utf-8"))
            except Exception:
                st = {}
            states_cache.append(st)

            tname = tdir.name
            dom = st.get("domain") or "?"
            sched_status = _query_schtasks_status(tname)
            ah_enabled = st.get("auto_heal", True)
            ah_badge = "[bold green]ON[/bold green]" if ah_enabled else "[dim]OFF[/dim]"
            h_count = str(st.get("heal_count") or 0)
            base_u = st.get("base_urls") or (st.get("urls") or [])[:2]
            base_str = "\n".join(base_u[:3]) + (f"\n(+{len(base_u) - 3} more)" if len(base_u) > 3 else "")

            last_log = "[dim]No ticks logged yet[/dim]"
            if lg_path.exists():
                lines = [ln.strip() for ln in lg_path.read_text(encoding="utf-8", errors="replace").splitlines() if ln.strip()]
                if lines:
                    last_log = lines[-1][-95:]

            table.add_row(str(idx), tname, dom, sched_status, ah_badge, h_count, base_str or "-", last_log)

        console.print(table)
        console.print(
            "\n  [bold cyan][h][/bold cyan] 🔄 Force Auto-Heal & Token Rotation Check (upgrades PS1 worker + polls live M365 CSS)\n"
            "  [bold cyan][r][/bold cyan] ▶️  Run a scheduled task immediately (schtasks /Run)\n"
            "  [bold cyan][l][/bold cyan] 📜 View task log & state JSON\n"
            "  [bold cyan][d][/bold cyan] 🗑️  Delete scheduled task (schtasks /Delete)\n"
            "  [bold cyan][b][/bold cyan] Back to TUI Menu"
        )
        sub = Prompt.ask("Select action", choices=["h", "r", "l", "d", "b"], default="b").strip().lower()
        if sub == "b":
            return

        sel_raw = Prompt.ask(
            f"Select task index (1-{len(task_dirs)}) or 'a' for all",
            default="1",
        ).strip().lower()
        if sel_raw in ("a", "all"):
            chosen_dirs = task_dirs
        elif sel_raw.isdigit() and 1 <= int(sel_raw) <= len(task_dirs):
            chosen_dirs = [task_dirs[int(sel_raw) - 1]]
        else:
            console.print("[yellow]Invalid task selection.[/yellow]")
            continue

        if sub == "h":
            for cdir in chosen_dirs:
                with console.status(f"[bold cyan]Checking live M365 CSS & auto-healing {cdir.name}...", spinner="dots"):
                    res_heal = heal_task_state_now(cdir)
                if not res_heal.get("ok"):
                    console.print(f"[bold red]✗ {cdir.name}:[/bold red] {res_heal.get('error')}")
                elif res_heal.get("rotated"):
                    console.print(
                        Panel(
                            f"[bold white on red] TOKEN ROTATION DETECTED & AUTO-HEALED [/bold white on red]\n\n"
                            f"Task      : [cyan]{cdir.name}[/cyan]\n"
                            f"Domain    : [bold white]{res_heal['domain']}[/bold white]\n"
                            f"Old Token : [dim]{', '.join(res_heal['old_base'])}[/dim]\n"
                            f"New Token : [bold green]{', '.join(res_heal['new_base'])}[/bold green]\n"
                            f"Pool Size : {res_heal['pool_size']} randomized URLs written to state JSON",
                            title=f"[bold green]✓ Auto-Heal Complete (#{res_heal['heal_count']})[/bold green]",
                            border_style="green",
                        )
                    )
                else:
                    console.print(
                        f"[bold green]✓ {cdir.name}:[/bold green] Worker upgraded to self-healing; "
                        f"token unchanged ({', '.join(res_heal['new_base']) or 'custom URL'})."
                    )
        elif sub == "r":
            for cdir in chosen_dirs:
                proc = subprocess.run(
                    ["schtasks", "/Run", "/TN", cdir.name],
                    capture_output=True,
                    text=True,
                    shell=False,
                )
                if proc.returncode == 0:
                    console.print(f"[bold green]✓ Triggered immediate run for {cdir.name}[/bold green]")
                else:
                    console.print(f"[bold red]✗ schtasks /Run failed for {cdir.name}:[/bold red] {proc.stderr.strip() or proc.stdout.strip()}")
        elif sub == "l":
            for cdir in chosen_dirs:
                st_path = cdir / "m365_canary_recurring.json"
                lg_path = cdir / "m365_canary_recurring.log"
                if st_path.exists():
                    console.print(
                        Panel(
                            Syntax(st_path.read_text(encoding="utf-8"), "json", theme="monokai", word_wrap=True),
                            title=f"[bold cyan]State JSON — {cdir.name}[/bold cyan]",
                            border_style="cyan",
                        )
                    )
                if lg_path.exists():
                    lines = lg_path.read_text(encoding="utf-8", errors="replace").splitlines()[-40:]
                    console.print(
                        Panel(
                            "\n".join(lines) or "[dim](empty log)[/dim]",
                            title=f"[bold magenta]Last 40 Log Entries — {lg_path}[/bold magenta]",
                            border_style="magenta",
                        )
                    )
                else:
                    console.print(f"[yellow]No log file yet at {lg_path}[/yellow]")
        elif sub == "d":
            for cdir in chosen_dirs:
                proc = subprocess.run(
                    ["schtasks", "/Delete", "/TN", cdir.name, "/F"],
                    capture_output=True,
                    text=True,
                    shell=False,
                )
                if proc.returncode == 0:
                    console.print(f"[bold green]✓ Removed scheduled task '{cdir.name}' from Windows Task Scheduler.[/bold green]")
                else:
                    console.print(f"[yellow]Task '{cdir.name}' was not registered in Scheduler ({proc.stderr.strip()}).[/yellow]")
                if Confirm.ask(f"Also delete task folder {cdir} from disk?", default=False):
                    import shutil
                    shutil.rmtree(cdir, ignore_errors=True)
                    console.print(f"[dim]Deleted folder {cdir}[/dim]")


# ---------------------------------------------------------------------------
# [10]–[14] Extended M365 / Entra ID Reconnaissance & Assessment Suite
# ---------------------------------------------------------------------------


def probe_tenant_guid(domain: str) -> Optional[str]:
    """From recon-tool/Claude-OSINT: parse GUID from OIDC issuer."""
    url = f"{BASE_URL}/{domain}/.well-known/openid-configuration"
    try:
        r = requests.get(url, headers=HEADERS, timeout=REQUEST_TIMEOUT)
        if r.status_code != 200:
            return None
        issuer = r.json().get("issuer", "")
        match = re.search(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", issuer, re.I)
        return match.group(0) if match else None
    except Exception:
        return None


def recon_getuserrealm(domain: str) -> dict:
    """UserRealm endpoint discovery: managed vs federated, STS URL, federation brand."""
    res = recon_domain_realm(domain)
    return annotate_with_risk("recon_getuserrealm", res)


def probe_autodiscover(domain: str) -> dict:
    """Autodiscover V2 JSON endpoint presence and service discovery."""
    clean_dom = domain.strip().lower().split("@")[-1]
    url = f"https://autodiscover-s.outlook.com/autodiscover/autodiscover.json?Email=probe@{quote(clean_dom)}&Protocol=Autodiscoverv1"
    try:
        r = requests.get(url, headers=HEADERS, timeout=REQUEST_TIMEOUT, allow_redirects=False)
        res = {
            "service": "Autodiscover V2",
            "url": r.url,
            "status_code": r.status_code,
            "exchange_online": r.status_code == 200,
            "response": r.json() if r.status_code == 200 else None,
        }
    except Exception as exc:
        res = {"service": "Autodiscover V2", "error": str(exc)}
    return annotate_with_risk("probe_autodiscover", res)


def probe_ews() -> dict:
    """Exchange Web Services (EWS) endpoint availability check."""
    url = "https://outlook.office365.com/EWS/Exchange.asmx"
    try:
        r = requests.get(url, headers=HEADERS, timeout=REQUEST_TIMEOUT, allow_redirects=False)
        res = {
            "service": "Exchange Web Services (EWS)",
            "url": url,
            "status_code": r.status_code,
            "exposed": r.status_code in (200, 301, 302, 401, 403),
            "auth_scheme": r.headers.get("Www-Authenticate", "None"),
        }
    except Exception as exc:
        res = {"service": "Exchange Web Services (EWS)", "error": str(exc)}
    return annotate_with_risk("probe_ews", res)


def probe_sharepoint(tenant_stem: str) -> List[dict]:
    """SharePoint root, personal OneDrive (-my), and Admin Center presence review."""
    findings = []
    for suffix, label in [("", "SharePoint Root"), ("-my", "OneDrive Personal"), ("-admin", "SharePoint Admin Center")]:
        url = f"https://{tenant_stem}{suffix}.sharepoint.com"
        try:
            r = requests.head(url, headers=HEADERS, timeout=8, allow_redirects=False)
            item = {
                "service": label,
                "url": url,
                "status_code": r.status_code,
                "build": r.headers.get("MicrosoftSharePointTeamServices", "Present"),
            }
        except Exception as exc:
            item = {"service": label, "url": url, "error": str(exc)}
        findings.append(item)
    return annotate_with_risk("probe_sharepoint", findings)


def probe_azure_services(tenant_stem: str) -> List[dict]:
    """DNS permutation review for Azure PaaS services (API Management, Key Vault, Blob Storage, App Service)."""
    permutations = [
        ("Azure API Management", f"{tenant_stem}.azure-api.net"),
        ("Azure Key Vault", f"{tenant_stem}.vault.azure.net"),
        ("Azure Blob Storage", f"{tenant_stem}.blob.core.windows.net"),
        ("Azure App Service", f"{tenant_stem}.azurewebsites.net"),
        ("Azure Traffic Manager", f"{tenant_stem}.trafficmanager.net"),
        ("Azure Cosmos DB", f"{tenant_stem}.documents.azure.com"),
    ]
    results = []
    for service_name, hostname in permutations:
        try:
            ip = socket.gethostbyname(hostname)
            results.append({"service": service_name, "hostname": hostname, "resolves": True, "ip": ip})
        except socket.gaierror:
            results.append({"service": service_name, "hostname": hostname, "resolves": False, "ip": None})
        except Exception as exc:
            results.append({"service": service_name, "hostname": hostname, "error": str(exc)})
    return annotate_with_risk("probe_azure_services", results)


def probe_lync(domain: str) -> dict:
    """SIP / Skype for Business / Teams autodiscover discovery and X-MS-WebTicketURL check."""
    clean_dom = domain.strip().lower().split("@")[-1]
    url = f"https://lyncdiscover.{clean_dom}"
    try:
        r = requests.get(url, headers=HEADERS, timeout=8, allow_redirects=False)
        ticket_url = r.headers.get("X-MS-WebTicketURL")
        res = {
            "service": "Lync / SfB Autodiscover",
            "url": url,
            "status_code": r.status_code,
            "web_ticket_url": ticket_url,
            "present": r.status_code < 500,
        }
    except Exception as exc:
        res = {"service": "Lync / SfB Autodiscover", "url": url, "error": str(exc), "present": False}
    return annotate_with_risk("probe_lync", res)


def probe_dynamics(tenant_stem: str) -> List[dict]:
    """Dynamics CRM instance hostname (CRM 1-10) DNS resolution inventory."""
    results = []
    for i in range(1, 11):
        host = f"{tenant_stem}.crm{i}.dynamics.com" if i > 1 else f"{tenant_stem}.crm.dynamics.com"
        try:
            ip = socket.gethostbyname(host)
            results.append({"crm_instance": f"CRM {i}", "hostname": host, "resolves": True, "ip": ip})
        except socket.gaierror:
            results.append({"crm_instance": f"CRM {i}", "hostname": host, "resolves": False, "ip": None})
        except Exception as exc:
            results.append({"crm_instance": f"CRM {i}", "hostname": host, "error": str(exc)})
    return annotate_with_risk("probe_dynamics", results)


def run_roadrecon(access_token: str) -> dict:
    """Authenticated Entra ID directory inventory helper using ROADrecon CLI."""
    log_session_activity("Microsoft Graph", "run_roadrecon directory inventory")
    tool = shutil.which("roadrecon")
    if not tool:
        return {
            "error": "ROADrecon is not installed or not found on PATH. Install it using: pip install roadrecon",
            "rc": -1,
            "stdout": "",
        }
    try:
        cmd = [tool, "gather", "--access-token", access_token.strip()]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
        return {"rc": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}
    except Exception as exc:
        return {"error": str(exc), "rc": -1, "stdout": ""}


def run_azurehound(username: Optional[str] = None, password: Optional[str] = None, tenant: Optional[str] = None, output: Optional[str] = None) -> dict:
    """Authenticated Entra ID structure inventory helper using AzureHound CLI."""
    log_session_activity(tenant or "Azure/Entra", "run_azurehound directory inventory")
    tool = shutil.which("azurehound")
    if not tool:
        return {
            "error": "AzureHound binary is not installed or not found on PATH. Download from: https://github.com/BloodHoundAD/AzureHound",
            "rc": -1,
            "stdout": "",
        }
    cmd = [tool, "-t", tenant or "common", "list"]
    if username:
        cmd.extend(["-u", username])
    if password:
        cmd.extend(["-p", password])
    if output:
        cmd.extend(["-o", output])
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
        return {"rc": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}
    except Exception as exc:
        return {"error": str(exc), "rc": -1, "stdout": ""}


# ---------------------------------------------------------------------------
# Internal Posture Audit Functions (Defensive & Native Commands)
# ---------------------------------------------------------------------------

def _python_socket_port_scan(target_ip: str, ports: List[int], timeout: float = 0.4) -> List[dict]:
    open_ports = []
    service_map = {
        21: "FTP", 22: "SSH", 23: "Telnet", 25: "SMTP", 53: "DNS",
        80: "HTTP", 88: "Kerberos", 110: "POP3", 135: "MS-RPC", 139: "NetBIOS",
        143: "IMAP", 389: "LDAP", 443: "HTTPS", 445: "SMB", 465: "SMTPS",
        587: "SMTP-Submission", 636: "LDAPS", 993: "IMAPS", 995: "POP3S",
        1433: "MSSQL", 3268: "LDAP-GC", 3269: "LDAPS-GC", 3389: "RDP",
        5985: "WinRM-HTTP", 5986: "WinRM-HTTPS", 8080: "HTTP-Proxy", 8443: "HTTPS-Alt",
    }
    for port in ports:
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.settimeout(timeout)
                res = s.connect_ex((target_ip, port))
                if res == 0:
                    banner = ""
                    try:
                        s.settimeout(0.5)
                        if port in (80, 8080):
                            s.sendall(b"HEAD / HTTP/1.0\r\n\r\n")
                            data = s.recv(256).decode("utf-8", errors="replace")
                            for line in data.splitlines():
                                if line.lower().startswith("server:"):
                                    banner = line.split(":", 1)[1].strip()
                                    break
                        elif port in (21, 22, 25):
                            banner = s.recv(256).decode("utf-8", errors="replace").strip()
                    except Exception:
                        pass
                    open_ports.append({
                        "port": port,
                        "service": service_map.get(port, "Unknown"),
                        "banner": banner or "Open",
                    })
        except Exception:
            pass
    return open_ports


def internal_nmap_discover(subnet: str, output_dir: Optional[str] = None) -> dict:
    """Two-phase network discovery: fast port discovery followed by service/version detection."""
    log_session_activity(subnet, f"internal_nmap_discover on {subnet}")
    tool = shutil.which("nmap")
    out_dir = Path(output_dir).expanduser() if output_dir else Path.cwd()
    out_dir.mkdir(parents=True, exist_ok=True)
    out_file = out_dir / "nmap_discovery.txt"

    if tool:
        try:
            # Phase 1: fast discovery
            cmd1 = [tool, "-F", "-T4", subnet, "-oN", str(out_file)]
            proc1 = subprocess.run(cmd1, capture_output=True, text=True, timeout=300)
            # Phase 2: service detection on discovered targets
            cmd2 = [tool, "-sV", "--version-light", "-T4", subnet, "-oN", str(out_dir / "nmap_services.txt")]
            proc2 = subprocess.run(cmd2, capture_output=True, text=True, timeout=300)
            return annotate_with_risk("internal_nmap_discover", {
                "engine": "nmap",
                "phase1_output": proc1.stdout,
                "phase2_output": proc2.stdout,
                "output_file": str(out_file),
                "rc": proc2.returncode,
            })
        except Exception as exc:
            return annotate_with_risk("internal_nmap_discover", {"error": str(exc), "rc": -1})

    # Resilient fallback: Pure-Python concurrent socket discovery & banner analysis
    common_audit_ports = [21, 22, 53, 80, 88, 135, 139, 389, 443, 445, 636, 1433, 3268, 3269, 3389, 5985, 5986, 8080, 8443]
    try:
        if "/" in subnet:
            net = ipaddress.ip_network(subnet, strict=False)
            hosts = [str(ip) for ip in net.hosts()][:256]
        else:
            hosts = [subnet.strip()]
    except Exception as exc:
        return annotate_with_risk("internal_nmap_discover", {"error": f"Invalid target or subnet '{subnet}': {exc}", "rc": -1})

    findings_summary = []
    raw_lines = [
        f"# Internal Discovery Report (Pure-Python Native Socket Scanner)",
        f"# Target / Subnet: {subnet} | Scanned Hosts: {len(hosts)}",
        f"# Timestamp: {datetime.now(timezone.utc).isoformat()}",
        "",
    ]

    with ThreadPoolExecutor(max_workers=32) as executor:
        futures = {executor.submit(_python_socket_port_scan, h, common_audit_ports): h for h in hosts}
        for fut in futures:
            h = futures[fut]
            try:
                open_ports = fut.result()
                if open_ports:
                    ports_desc = ", ".join(f"{p['port']}/{p['service']} ({p['banner']})" for p in open_ports)
                    findings_summary.append(f"{h}: {ports_desc}")
                    raw_lines.append(f"Host: {h}")
                    for p in open_ports:
                        raw_lines.append(f"  Port: {p['port']}/tcp  Service: {p['service']:<14}  Banner: {p['banner']}")
                    raw_lines.append("")
            except Exception:
                pass

    out_file.write_text("\n".join(raw_lines), encoding="utf-8")
    return annotate_with_risk("internal_nmap_discover", {
        "engine": "Native Python Socket Scanner (Nmap binary not found)",
        "hosts_scanned": len(hosts),
        "live_hosts_with_open_ports": len(findings_summary),
        "discovered_endpoints": findings_summary if findings_summary else ["No open standard ports detected across scanned hosts"],
        "output_file": str(out_file),
        "rc": 0,
    })


def internal_dns_enum(dns_server: str, domain: str) -> dict:
    """Internal DNS service records, zone transfer check, and reverse lookup inventory."""
    log_session_activity(dns_server, f"internal_dns_enum for {domain}")
    results = {"srv_records": [], "zone_transfer": "Failed / Refused", "reverse_lookups": []}
    srv_queries = [
        f"_ldap._tcp.{domain}",
        f"_kerberos._tcp.{domain}",
        f"_kpasswd._tcp.{domain}",
        f"_gc._tcp.{domain}",
        f"_sipfederationtls._tcp.{domain}",
    ]
    tool = shutil.which("nslookup")
    for srv in srv_queries:
        if tool:
            try:
                proc = subprocess.run([tool, f"-type=SRV", srv, dns_server], capture_output=True, text=True, timeout=8)
                if "mail addr" in proc.stdout or "svr hostname" in proc.stdout or "service" in proc.stdout:
                    results["srv_records"].append({"record": srv, "response": proc.stdout.strip()})
            except Exception:
                pass

    # Zone transfer verification
    if tool:
        try:
            cmd = f"server {dns_server}\nls -d {domain}\nexit"
            proc = subprocess.run([tool], input=cmd, capture_output=True, text=True, timeout=10)
            if "Transfer failed" not in proc.stdout and "Refused" not in proc.stdout and domain in proc.stdout:
                results["zone_transfer"] = "Succeeded (Zone transfer OPEN - Remediation Required)"
        except Exception:
            pass

    return annotate_with_risk("internal_dns_enum", results)


def internal_directory_enum(dc_host: str, domain: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only LDAP queries: admin groups, user inventory, computer objects, trust relationships."""
    log_session_activity(dc_host, f"internal_directory_enum on {domain}")
    # On Windows, try native PowerShell ActiveDirectory module or System.DirectoryServices
    if platform.system().lower() == "windows":
        ps_script = f"""
        $ErrorActionPreference = 'SilentlyContinue'
        $results = @{{
            AdminUsers = @()
            TotalUsers = 0
            Computers = @()
            Trusts = @()
        }}
        if (Get-Command Get-ADGroupMember -ErrorAction SilentlyContinue) {{
            $results.AdminUsers = @(Get-ADGroupMember 'Domain Admins' | Select-Object -ExpandProperty SamAccountName)
            $results.TotalUsers = (Get-ADUser -Filter *).Count
            $results.Computers = @(Get-ADComputer -Filter * | Select-Object -ExpandProperty Name)
            $results.Trusts = @(Get-ADTrust -Filter * | Select-Object -ExpandProperty Name)
        }} else {{
            $searcher = [adsisearcher]"(objectClass=user)"
            $searcher.PageSize = 100
            $results.TotalUsers = ($searcher.FindAll()).Count
        }}
        $results | ConvertTo-Json -Depth 3
        """
        try:
            proc = subprocess.run(["powershell", "-NoProfile", "-Command", ps_script], capture_output=True, text=True, timeout=30)
            if proc.stdout.strip().startswith("{"):
                return annotate_with_risk("internal_directory_enum", json.loads(proc.stdout))
        except Exception as exc:
            logger.debug(f"PowerShell AD enum failed: {exc}")

    # Fallback to ldapsearch
    tool = shutil.which("ldapsearch")
    if not tool:
        return {"error": "ActiveDirectory module and ldapsearch utility not found. Please install ldap-utils or run on a domain-joined host.", "rc": -1}
    try:
        cmd = [tool, "-x", "-h", dc_host, "-b", f"DC={domain.replace('.', ',DC=')}", "(objectClass=user)", "sAMAccountName"]
        if username and password:
            cmd.extend(["-D", f"{username}@{domain}", "-w", password])
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=20)
        users = re.findall(r"sAMAccountName:\\s*(\\S+)", proc.stdout)
        return annotate_with_risk("internal_directory_enum", {"TotalUsers": len(users), "SampleUsers": users[:15]})
    except Exception as exc:
        return {"error": str(exc), "rc": -1}


def internal_spn_inventory(dc_host: str, domain: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only inventory of user accounts configured with ServicePrincipalNames (gMSA migration planning)."""
    log_session_activity(dc_host, f"internal_spn_inventory for {domain}")
    if platform.system().lower() == "windows":
        ps_cmd = "Get-ADUser -Filter {ServicePrincipalName -like '*'} -Properties ServicePrincipalName, PasswordLastSet | Select-Object SamAccountName, PasswordLastSet, ServicePrincipalName | ConvertTo-Json -Depth 3"
        try:
            proc = subprocess.run(["powershell", "-NoProfile", "-Command", ps_cmd], capture_output=True, text=True, timeout=25)
            if proc.stdout.strip():
                data = json.loads(proc.stdout)
                items = data if isinstance(data, list) else [data]
                return annotate_with_risk("internal_spn_inventory", {"accounts": items, "count": len(items)})
        except Exception:
            pass

    tool = shutil.which("ldapsearch")
    if tool:
        try:
            filter_q = "(&(objectClass=user)(servicePrincipalName=*)(!(objectClass=computer)))"
            cmd = [tool, "-x", "-h", dc_host, "-b", f"DC={domain.replace('.', ',DC=')}", filter_q, "sAMAccountName", "servicePrincipalName"]
            if username and password:
                cmd.extend(["-D", f"{username}@{domain}", "-w", password])
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=20)
            spns = re.findall(r"sAMAccountName:\\s*(\\S+)", proc.stdout)
            return annotate_with_risk("internal_spn_inventory", {"accounts": [{"SamAccountName": u} for u in spns], "count": len(spns)})
        except Exception as exc:
            return {"error": str(exc)}

    return {"error": "Neither PowerShell AD module nor ldapsearch available for SPN inventory."}


def internal_preauth_inventory(dc_host: str, domain: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only inventory of user accounts with DONT_REQ_PREAUTH flag enabled."""
    log_session_activity(dc_host, f"internal_preauth_inventory for {domain}")
    if platform.system().lower() == "windows":
        ps_cmd = "Get-ADUser -Filter {DoesNotRequirePreAuth -eq $true} -Properties DoesNotRequirePreAuth, PasswordLastSet | Select-Object SamAccountName, PasswordLastSet | ConvertTo-Json -Depth 3"
        try:
            proc = subprocess.run(["powershell", "-NoProfile", "-Command", ps_cmd], capture_output=True, text=True, timeout=25)
            if proc.stdout.strip():
                data = json.loads(proc.stdout)
                items = data if isinstance(data, list) else [data]
                return annotate_with_risk("internal_preauth_inventory", {"accounts": items, "count": len(items)})
        except Exception:
            pass

    tool = shutil.which("ldapsearch")
    if tool:
        try:
            # 4194304 = 0x400000 = DONT_REQ_PREAUTH
            filter_q = "(&(objectClass=user)(userAccountControl:1.2.840.113556.1.4.803:=4194304))"
            cmd = [tool, "-x", "-h", dc_host, "-b", f"DC={domain.replace('.', ',DC=')}", filter_q, "sAMAccountName"]
            if username and password:
                cmd.extend(["-D", f"{username}@{domain}", "-w", password])
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=20)
            preauth_users = re.findall(r"sAMAccountName:\\s*(\\S+)", proc.stdout)
            return annotate_with_risk("internal_preauth_inventory", {"accounts": [{"SamAccountName": u} for u in preauth_users], "count": len(preauth_users)})
        except Exception as exc:
            return {"error": str(exc)}

    return {"error": "Neither PowerShell AD module nor ldapsearch available for pre-auth inventory."}


def internal_share_inventory(subnet_or_host: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only SMB share listing and permission review."""
    log_session_activity(subnet_or_host, "internal_share_inventory")
    if platform.system().lower() == "windows":
        ps_cmd = f"Get-SmbShare -CimSession {subnet_or_host} | Select-Object Name, Path, Description | ConvertTo-Json -Depth 3"
        try:
            proc = subprocess.run(["powershell", "-NoProfile", "-Command", ps_cmd], capture_output=True, text=True, timeout=20)
            if proc.stdout.strip().startswith("{") or proc.stdout.strip().startswith("["):
                data = json.loads(proc.stdout)
                shares = data if isinstance(data, list) else [data]
                return annotate_with_risk("internal_share_inventory", {"shares": shares, "target": subnet_or_host})
        except Exception:
            pass

    tool = shutil.which("smbclient")
    if tool:
        cmd = [tool, "-L", f"\\{subnet_or_host}", "-N"]
        if username and password:
            cmd = [tool, "-L", f"\\{subnet_or_host}", "-U", f"{username}%{password}"]
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=15)
            shares = []
            for line in proc.stdout.splitlines():
                if "Disk" in line or "IPC" in line:
                    shares.append({"share_line": line.strip()})
            return annotate_with_risk("internal_share_inventory", {"shares": shares, "raw": proc.stdout, "target": subnet_or_host})
        except Exception as exc:
            return {"error": str(exc)}

    return {"error": "smbclient or Get-SmbShare not available for share inventory."}


def internal_sysvol_cpassword_scan(dc_host: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only scan of SYSVOL XML files for presence of legacy GPP cpassword attributes without decrypting."""
    log_session_activity(dc_host, "internal_sysvol_cpassword_scan")
    findings = []
    # Check via UNC path if on Windows
    sysvol_unc = Path(fr"\\{dc_host}\SYSVOL")
    if sysvol_unc.exists():
        try:
            for xml_file in sysvol_unc.rglob("*.xml"):
                try:
                    text = xml_file.read_text(encoding="utf-8", errors="ignore")
                    if "cpassword" in text.lower():
                        acct_match = re.search(r'userName="([^"]+)"', text, re.IGNORECASE)
                        findings.append({
                            "filename": xml_file.name,
                            "path": str(xml_file),
                            "account": acct_match.group(1) if acct_match else "Unknown",
                            "finding": "Legacy GPP credential attribute detected. Requires immediate file removal.",
                        })
                except Exception:
                    pass
            return annotate_with_risk("internal_sysvol_cpassword_scan", {"findings": findings, "count": len(findings)})
        except Exception as exc:
            return {"error": str(exc)}

    return annotate_with_risk("internal_sysvol_cpassword_scan", {
        "findings": findings,
        "count": 0,
        "notes": fr"SYSVOL share at \\{dc_host}\SYSVOL was not directly reachable over SMB.",
    })


def internal_certificate_template_audit(dc_host: str, domain: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only review of AD CS certificate templates for ENROLLEE_SUPPLIES_SUBJECT and weak enrollment policies."""
    log_session_activity(dc_host, "internal_certificate_template_audit")
    tool = shutil.which("certutil")
    if tool:
        try:
            proc = subprocess.run([tool, "-template"], capture_output=True, text=True, timeout=20)
            vulnerable_templates = []
            for block in proc.stdout.split("\n\n"):
                if "CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT" in block or "Client Authentication" in block:
                    name_match = re.search(r"TemplatePropCommonName\\s*=\\s*(\\S+)", block)
                    if name_match:
                        vulnerable_templates.append({
                            "template_name": name_match.group(1),
                            "finding": "ENROLLEE_SUPPLIES_SUBJECT flag detected in template configuration.",
                        })
            return annotate_with_risk("internal_certificate_template_audit", {"templates": vulnerable_templates, "count": len(vulnerable_templates)})
        except Exception:
            pass

    # Certipy read-only review check
    certipy_tool = shutil.which("certipy")
    if certipy_tool:
        try:
            cmd = [certipy_tool, "find", "-target", dc_host, "-stdout", "-vulnerable"]
            if username and password:
                cmd.extend(["-u", f"{username}@{domain}", "-p", password])
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
            return annotate_with_risk("internal_certificate_template_audit", {"stdout": proc.stdout, "count": 1 if "ESC" in proc.stdout else 0})
        except Exception as exc:
            return {"error": str(exc)}

    return {"error": "Neither certutil nor certipy available for certificate template audit."}


def internal_session_inventory(target_host: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only logged-on account and session inventory via quser/qwinsta or rpcclient."""
    log_session_activity(target_host, "internal_session_inventory")
    if platform.system().lower() == "windows":
        tool = shutil.which("quser") or shutil.which("qwinsta")
        if tool:
            try:
                proc = subprocess.run([tool, f"/server:{target_host}"], capture_output=True, text=True, timeout=10)
                sessions = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
                return annotate_with_risk("internal_session_inventory", {"sessions": sessions, "target": target_host})
            except Exception as exc:
                return {"error": str(exc)}

    rpc_tool = shutil.which("rpcclient")
    if rpc_tool:
        try:
            cmd = [rpc_tool, "-U", f"{username}%{password}" if username and password else "%", "-c", "netsessionenum", target_host]
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=10)
            return annotate_with_risk("internal_session_inventory", {"sessions": proc.stdout.splitlines(), "target": target_host})
        except Exception as exc:
            return {"error": str(exc)}

    return {"error": "Session query tools (quser, qwinsta, rpcclient) not available."}


def internal_path_mapping(domain: str, username: Optional[str] = None, password: Optional[str] = None) -> dict:
    """Read-only directory relationship collection for offline posture review via BloodHound collector."""
    log_session_activity(domain, "internal_path_mapping")
    tool = shutil.which("bloodhound-python")
    if not tool:
        return {"error": "bloodhound-python is not installed. Install via: pip install bloodhound", "rc": -1}
    cmd = [tool, "-d", domain, "-c", "All", "--zip"]
    if username:
        cmd.extend(["-u", username])
    if password:
        cmd.extend(["-p", password])
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
        return {"rc": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}
    except Exception as exc:
        return {"error": str(exc), "rc": -1}


def recon_openid(domain: str) -> dict:
    """Port of MSFTRecon OpenID discovery + AADInternals tenant info."""
    url = f"{BASE_URL}/{domain}/.well-known/openid-configuration"
    try:
        r = requests.get(url, headers=HEADERS, timeout=REQUEST_TIMEOUT)
        if r.status_code != 200:
            return {"error": f"HTTP {r.status_code}"}
        data = r.json()
        issuer = data.get("issuer", "")
        return {
            "tenant_id": issuer.rstrip("/").split("/")[-1] if issuer else None,
            "tenant_region_scope": data.get("tenant_region_scope"),
            "tenant_region_sub_scope": data.get("tenant_region_sub_scope"),
            "cloud_instance_name": data.get("cloud_instance_name"),
            "authorization_endpoint": data.get("authorization_endpoint"),
            "token_endpoint": data.get("token_endpoint"),
            "device_authorization_endpoint": data.get("device_authorization_endpoint"),
        }
    except Exception as exc:
        return {"error": str(exc)}


def recon_domain_realm(domain: str) -> dict:
    """Port of RealmOsintEx / GetUserRealm.srf — Managed vs Federated, ADFS FQDN."""
    url = f"{BASE_URL}/GetUserRealm.srf?login=probe@{quote(domain)}&json=1"
    try:
        r = requests.get(url, headers=HEADERS, timeout=REQUEST_TIMEOUT)
        if r.status_code != 200:
            return {"error": f"HTTP {r.status_code}"}
        data = r.json()
        return {
            "namespace_type": data.get("NameSpaceType"),
            "federation_brand": data.get("FederationBrandName"),
            "auth_url": data.get("AuthURL"),
            "cloud_instance_name": data.get("CloudInstanceName"),
            "domain_name": data.get("DomainName"),
        }
    except Exception as exc:
        return {"error": str(exc)}


def recon_seamless_sso(domain: str) -> dict:
    """Check DesktopSSO/Seamless SSO status via WHR login page JS $Config + GetCredentialType."""
    whr_url = f"{BASE_URL}/?whr={domain}"
    r = make_request(whr_url)
    sso_enabled = None
    if r:
        match = re.search(r'"desktopSsoEnabled"\s*:\s*(true|false|1|0)', r.text, re.I)
        if match:
            sso_enabled = match.group(1).lower() in ("true", "1")
    if sso_enabled is None:
        cred = get_credential_type(f"probe@{domain}")
        if "error" not in cred:
            sso_enabled = bool(cred.get("EstsProperties", {}).get("DesktopSsoEnabled"))
    return {"seamless_sso_enabled": sso_enabled}


def run_full_tenant_recon(domain: str) -> TenantRecon:
    """Consolidate MSFTRecon + AADInternals ReconAsOutsider + RealmOsintEx + per-domain MX/SPF."""
    clean_dom = domain.strip().lower().split("@")[-1]
    tr = TenantRecon(domain=clean_dom)
    if not is_valid_domain(clean_dom):
        tr.error = f"Invalid domain: {clean_dom}"
        return tr

    with console.status(f"[bold cyan][10] Running Full Tenant Recon (MSFTRecon + AADInternals) on {clean_dom}...", spinner="dots"):
        oid = recon_openid(clean_dom)
        if "error" in oid:
            tr.error = oid["error"]
        else:
            tr.tenant_id = oid.get("tenant_id")
            tr.region = oid.get("tenant_region_scope")
            tr.cloud_instance = oid.get("cloud_instance_name")
            tr.auth_endpoints = {
                "authorization": oid.get("authorization_endpoint"),
                "token": oid.get("token_endpoint"),
                "device_code": oid.get("device_authorization_endpoint"),
            }

        realm = recon_domain_realm(clean_dom)
        if "error" not in realm:
            tr.namespace_type = realm.get("namespace_type")
            tr.federation_brand = realm.get("federation_brand")
            tr.brand_name = realm.get("federation_brand")
            tr.adfs_server = realm.get("auth_url")
            tr.cloud_instance = tr.cloud_instance or realm.get("cloud_instance_name")

        sso = recon_seamless_sso(clean_dom)
        tr.seamless_sso = sso.get("seamless_sso_enabled")
        tr.desktop_sso = tr.seamless_sso

        soap = get_federation_domains_soap(clean_dom)
        domains_list = soap.get("domains") or [clean_dom]
        for d in domains_list:
            if d.endswith(".onmicrosoft.com") and not d.endswith(".mail.onmicrosoft.com"):
                tr.tenant_name = d[: -len(".onmicrosoft.com")]
                break

        # Sample up to 25 verified domains for MX/SPF/Type table
        sample_domains = domains_list[:25]
        for d in sample_domains:
            mx_ans = doh_query(d, "MX")
            txt_ans = doh_query(d, "TXT")
            has_eop_mx = any("outlook.com" in (a.get("data") or "").lower() for a in mx_ans)
            has_spf = any("spf.protection.outlook.com" in (a.get("data") or "").lower() for a in txt_ans)
            tr.verified_domains.append(
                {
                    "name": d,
                    "mx_eop": has_eop_mx,
                    "spf_exo": has_spf,
                    "type": "Initial (.onmicrosoft)" if d.endswith(".onmicrosoft.com") else (tr.namespace_type or "Managed"),
                    "sts": tr.adfs_server or "Entra ID Cloud",
                }
            )

    return tr


def render_tenant_recon(recon: TenantRecon):
    console.print()
    if recon.error and not recon.tenant_id:
        console.print(Panel(f"[bold red]Error:[/bold red] {recon.error}", title=f"Full Tenant Recon — {recon.domain}", border_style="red"))
        return

    t = Table(box=box.ROUNDED, show_header=False, expand=True, border_style="cyan")
    t.add_column("Property", style="bold cyan", width=30)
    t.add_column("Value", style="white", overflow="fold")
    t.add_row("Target Domain", f"[bold white]{recon.domain}[/bold white]")
    t.add_row("Tenant ID (GUID)", f"[bold green]{recon.tenant_id or 'N/A'}[/bold green]")
    t.add_row("Tenant Name / Slug", f"[bold yellow]{recon.tenant_name or 'N/A'}[/bold yellow]")
    t.add_row("Federation Brand Name", recon.brand_name or "Not configured")
    t.add_row("Region / Cloud Instance", f"{recon.region or '?'} | {recon.cloud_instance or 'microsoftonline.com'}")
    t.add_row("Identity Namespace Type", recon.namespace_type or "Unknown")
    t.add_row("ADFS / Federated STS URL", recon.adfs_server or "[dim]None (Cloud Managed)[/dim]")
    t.add_row(
        "Seamless SSO (DesktopSSO)",
        "[bold green]ENABLED[/bold green]" if recon.seamless_sso else "[dim]Disabled / False[/dim]",
    )
    for k, ep in recon.auth_endpoints.items():
        if ep:
            t.add_row(f"OAuth {k.title()} Endpoint", f"[dim]{ep}[/dim]")

    console.print(Panel(t, title=f"[bold cyan][10] Full Tenant Recon (MSFTRecon + AADInternals) — {recon.domain}[/bold cyan]", border_style="cyan"))

    if recon.verified_domains:
        dt = Table(
            title=f"Verified Co-Tenant Domains Posture (Showing {len(recon.verified_domains)} domain(s))",
            box=box.HEAVY_HEAD,
            border_style="magenta",
            expand=True,
        )
        dt.add_column("Domain Name", style="bold white")
        dt.add_column("EOP MX", justify="center")
        dt.add_column("EXO SPF", justify="center")
        dt.add_column("Identity Type", style="cyan")
        dt.add_column("STS / IdP", style="yellow", overflow="fold")
        for row in recon.verified_domains:
            dt.add_row(
                row["name"],
                "[green]Yes[/green]" if row["mx_eop"] else "[dim]No[/dim]",
                "[green]Yes[/green]" if row["spf_exo"] else "[dim]No[/dim]",
                row["type"],
                row["sts"],
            )
        console.print(dt)


def enum_user_onedrive(tenant_name: str, users: List[str]) -> List[dict]:
    """
    Zero-audit-log user enumeration via OneDrive personal site.
    401/403 = user exists (personal site provisioned), 404 = absent.
    """
    results = []
    for u in users:
        local, _, domain = u.strip().partition("@")
        if not domain:
            continue
        domain_part = domain.replace(".", "_").replace("-", "_")
        local_part = re.sub(r"[^a-zA-Z0-9]", "_", local)
        url = f"https://{tenant_name}-my.sharepoint.com/personal/{local_part}_{domain_part}/_layouts/15/onedrive.aspx"
        try:
            r = requests.head(url, headers=HEADERS, timeout=10, allow_redirects=False)
            exists = r.status_code in (401, 403, 200, 302)
            results.append(
                {
                    "user": u,
                    "exists": exists if r.status_code != 404 else False,
                    "status_code": r.status_code,
                    "method": "OneDriveEnum",
                    "audit_logs": "None (Zero-Audit)",
                }
            )
        except Exception as exc:
            results.append({"user": u, "exists": None, "error": str(exc), "method": "OneDriveEnum", "audit_logs": "None"})
    return results


def enum_user_getcredentialtype(users: List[str], throttle: float = 0.3) -> List[dict]:
    """Port of o365creeper / AADInternals Normal method (IfExistsResult: 0=exists, 1=absent)."""
    results = []
    for idx, u in enumerate(users):
        if idx > 0 and throttle > 0:
            time.sleep(throttle)
        cred = get_credential_type(u.strip())
        if "error" in cred:
            results.append({"user": u, "exists": None, "error": cred["error"], "method": "GetCredentialType", "audit_logs": "Possible"})
            continue
        ier = cred.get("IfExistsResult")
        throttled = cred.get("ThrottleStatus") == 1
        exists = True if ier in (0, 5, 6) else (False if ier == 1 else None)
        results.append(
            {
                "user": u,
                "exists": exists,
                "status_code": f"IfExists={ier}",
                "throttled": throttled,
                "method": "GetCredentialType",
                "audit_logs": "Possible",
            }
        )
    return results


def enum_user_seamless_sso(domain: str, users: List[str]) -> List[dict]:
    """SeamlessSSO Autologon reachability & DesktopSSO check."""
    sso = recon_seamless_sso(domain)
    enabled = sso.get("seamless_sso_enabled")
    tenant_guid = probe_tenant_guid(domain)
    autologon_url = (
        f"https://autologon.microsoftazuread-sso.com/{tenant_guid}/winauth/trust/2005/usernamemixed?client-request-id=00000000-0000-0000-0000-000000000000"
        if tenant_guid
        else None
    )
    results = []
    for u in users:
        if not enabled or not autologon_url:
            results.append(
                {
                    "user": u,
                    "exists": False,
                    "status_code": "DesktopSSO Disabled",
                    "method": "SeamlessSSO-Autologon",
                    "audit_logs": "None",
                }
            )
            continue
        soap_probe = f"""<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:u="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd" xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd" xmlns:trust="http://schemas.xmlsoap.org/ws/2005/02/trust">
  <s:Header>
    <a:Action s:mustUnderstand="1">http://schemas.xmlsoap.org/ws/2005/02/trust/RST/Issue</a:Action>
    <a:To s:mustUnderstand="1">{autologon_url}</a:To>
    <wsse:Security><wsse:UsernameToken><wsse:Username>{u.strip()}</wsse:Username><wsse:Password>ProbePassword123!</wsse:Password></wsse:UsernameToken></wsse:Security>
  </s:Header>
  <s:Body><trust:RequestSecurityToken><wsp:AppliesTo xmlns:wsp="http://schemas.xmlsoap.org/ws/2004/09/policy"><a:EndpointReference><a:Address>urn:federation:MicrosoftOnline</a:Address></a:EndpointReference></wsp:AppliesTo><trust:RequestType>http://schemas.xmlsoap.org/ws/2005/02/trust/Issue</trust:RequestType></trust:RequestSecurityToken></s:Body>
</s:Envelope>"""
        try:
            r = requests.post(
                autologon_url,
                data=soap_probe,
                headers={"Content-Type": "application/soap+xml; charset=utf-8"},
                timeout=10,
            )
            # AADSTS50053 / AADSTS50126 / AADSTS50055 = user exists; AADSTS50034 = user does not exist
            if "AADSTS50034" in r.text:
                exists = False
                code_str = "AADSTS50034 (Absent)"
            elif any(code in r.text for code in ("AADSTS50126", "AADSTS50053", "AADSTS50055", "AADSTS50056", "AADSTS50076")):
                exists = True
                code_str = "AADSTS50126/Exists"
            else:
                exists = None
                code_str = f"HTTP {r.status_code}"
            results.append({"user": u, "exists": exists, "status_code": code_str, "method": "SeamlessSSO-Autologon", "audit_logs": "None"})
        except Exception as exc:
            results.append({"user": u, "exists": None, "error": str(exc), "method": "SeamlessSSO-Autologon", "audit_logs": "None"})
    return results


def render_user_enum(results: List[dict], domain: str):
    console.print()
    t = Table(title=f"[11] User Enumeration Results — {domain}", box=box.HEAVY_HEAD, border_style="bright_blue", expand=True)
    t.add_column("User / Email", style="bold white")
    t.add_column("Exists?", justify="center")
    t.add_column("Method", style="cyan")
    t.add_column("Status / Code", style="yellow")
    t.add_column("Audit Log Risk", style="magenta")

    for row in results:
        ex = row.get("exists")
        if ex is True:
            badge = "[bold white on green] VALID USER [/bold white on green]"
        elif ex is False:
            badge = "[dim red]Absent[/dim red]"
        else:
            badge = f"[yellow]{row.get('error') or 'Unknown'}[/yellow]"
        t.add_row(
            row.get("user", ""),
            badge,
            row.get("method", ""),
            str(row.get("status_code") or row.get("error") or "-"),
            str(row.get("audit_logs") or "-"),
        )
    console.print(t)


def probe_endpoints(domain: str, tenant_name: Optional[str] = None) -> List[dict]:
    """[12] Batch probe common M365 & Azure endpoint families."""
    clean_dom = domain.strip().lower().split("@")[-1]
    if not tenant_name:
        soap = get_federation_domains_soap(clean_dom)
        for d in soap.get("domains", []):
            if d.endswith(".onmicrosoft.com") and not d.endswith(".mail.onmicrosoft.com"):
                tenant_name = d[: -len(".onmicrosoft.com")]
                break

    findings: List[dict] = []

    # Autodiscover V2
    try:
        r = requests.get(
            f"https://autodiscover-s.outlook.com/autodiscover/autodiscover.json?Email=probe@{quote(clean_dom)}&Protocol=Autodiscoverv1",
            headers=HEADERS,
            timeout=10,
            allow_redirects=False,
        )
        findings.append(
            {
                "service": "Autodiscover V2",
                "target": r.url,
                "status": str(r.status_code),
                "notes": "Exchange Online active" if r.status_code == 200 else f"HTTP {r.status_code}",
            }
        )
    except Exception as exc:
        findings.append({"service": "Autodiscover V2", "target": clean_dom, "status": "ERR", "notes": str(exc)})

    # EWS
    try:
        r = requests.get("https://outlook.office365.com/EWS/Exchange.asmx", headers=HEADERS, timeout=10, allow_redirects=False)
        exposed = r.status_code in (200, 301, 302, 401, 403)
        findings.append(
            {
                "service": "Exchange EWS",
                "target": "https://outlook.office365.com/EWS/Exchange.asmx",
                "status": str(r.status_code),
                "notes": "Exposed (Auth Gate)" if exposed else "Closed",
            }
        )
    except Exception as exc:
        findings.append({"service": "Exchange EWS", "target": "outlook.office365.com", "status": "ERR", "notes": str(exc)})

    # SharePoint & OneDrive & Admin
    if tenant_name:
        for suffix, label in [("", "SharePoint Root"), ("-my", "OneDrive Personal"), ("-admin", "SharePoint Admin Center")]:
            url = f"https://{tenant_name}{suffix}.sharepoint.com"
            try:
                r = requests.head(url, headers=HEADERS, timeout=8, allow_redirects=False)
                findings.append(
                    {
                        "service": label,
                        "target": url,
                        "status": str(r.status_code),
                        "notes": f"Build: {r.headers.get('MicrosoftSharePointTeamServices', 'Active')}",
                    }
                )
            except Exception as exc:
                findings.append({"service": label, "target": url, "status": "ERR", "notes": str(exc)})

    # Lync/SfB discover
    try:
        r = requests.get(f"https://lyncdiscover.{clean_dom}", headers=HEADERS, timeout=7, allow_redirects=False)
        findings.append(
            {
                "service": "Lync / SfB Discover",
                "target": f"https://lyncdiscover.{clean_dom}",
                "status": str(r.status_code),
                "notes": r.headers.get("X-MS-WebTicketURL") or "Reachable",
            }
        )
    except Exception:
        findings.append({"service": "Lync / SfB Discover", "target": f"lyncdiscover.{clean_dom}", "status": "Unreachable", "notes": "No Lync endpoint"})

    # Azure service DNS permutations
    if tenant_name:
        azure_probes = [
            f"{tenant_name}.azure-api.net",
            f"{tenant_name}.trafficmanager.net",
            f"{tenant_name}.vault.azure.net",
            f"{tenant_name}.blob.core.windows.net",
            f"{tenant_name}.azurewebsites.net",
            f"{tenant_name}.documents.azure.com",
        ]
        for host in azure_probes:
            try:
                ip = socket.gethostbyname(host)
                findings.append({"service": "Azure DNS Permutation", "target": host, "status": "RESOLVES", "notes": f"IP: {ip}"})
            except socket.gaierror:
                findings.append({"service": "Azure DNS Permutation", "target": host, "status": "NXDOMAIN", "notes": "Not provisioned"})

    return findings


def render_endpoint_findings(findings: List[dict], domain: str):
    console.print()
    t = Table(title=f"[12] M365 & Azure Endpoint Discovery — {domain}", box=box.HEAVY_HEAD, border_style="cyan", expand=True)
    t.add_column("Service Family", style="bold cyan", width=24)
    t.add_column("Endpoint / Host", style="white", overflow="fold")
    t.add_column("Status", justify="center", width=14)
    t.add_column("Telemetry / Notes", style="yellow", overflow="fold")

    for f in findings:
        st = str(f.get("status", ""))
        if st in ("200", "301", "302", "401", "403", "RESOLVES"):
            st_badge = f"[bold green]{st}[/bold green]"
        elif st in ("404", "NXDOMAIN", "Unreachable"):
            st_badge = f"[dim]{st}[/dim]"
        else:
            st_badge = f"[yellow]{st}[/yellow]"
        t.add_row(f.get("service", ""), f.get("target", ""), st_badge, f.get("notes", ""))
    console.print(t)


def render_risk_matrix():
    """[13] Render EvilMist OPSEC Detection Risk Matrix."""
    console.print()
    t = Table(title="[13] M365 Reconnaissance OPSEC & Detection Risk Matrix (EvilMist Reference)", box=box.HEAVY_HEAD, border_style="magenta", expand=True)
    t.add_column("Reconnaissance Method", style="bold white", width=30)
    t.add_column("Detection Risk", justify="center", width=16)
    t.add_column("Entra / M365 Audit Logs", justify="center", width=22)
    t.add_column("Operational Notes", style="cyan", overflow="fold")

    for row in DETECTION_RISK_MATRIX:
        r = row["risk"]
        if r == "None":
            r_badge = "[bold white on green] NONE (SILENT) [/bold white on green]"
        elif r == "Very Low":
            r_badge = "[bold green]Very Low[/bold green]"
        elif r == "Low":
            r_badge = "[bold yellow]Low[/bold yellow]"
        else:
            r_badge = f"[bold red]{r}[/bold red]"
        t.add_row(row["method"], r_badge, row["audit_logs"], row["notes"])
    console.print(t)


def enumerate_graph_with_token(token: str, resource: str = "users") -> dict:
    """[14] Lightweight Microsoft Graph enumeration compatible with EntraEnum."""
    headers = {"Authorization": f"Bearer {token.strip()}", "Accept": "application/json"}
    url = f"https://graph.microsoft.com/v1.0/{resource}"
    try:
        r = requests.get(url, headers=headers, timeout=REQUEST_TIMEOUT)
        if r.status_code == 401:
            return {"resource": resource, "error": "HTTP 401 — Token invalid or expired"}
        if r.status_code == 403:
            return {"resource": resource, "error": "HTTP 403 — Insufficient scope for resource"}
        r.raise_for_status()
        data = r.json()
        items = data.get("value", [])
        return {"resource": resource, "count": len(items), "sample": items[:5]}
    except Exception as exc:
        return {"resource": resource, "error": str(exc)}


def run_graph_suite(token: str) -> List[dict]:
    resources = ["users", "groups", "applications", "servicePrincipals", "devices", "directoryRoles"]
    out = []
    for res_name in resources:
        out.append(enumerate_graph_with_token(token, res_name))
    return out


def render_graph_enumeration(results: List[dict]):
    console.print()
    road_bin = shutil.which("roadrecon")
    t = Table(title="[14] Authenticated Microsoft Graph Directory Enumeration", box=box.HEAVY_HEAD, border_style="green", expand=True)
    t.add_column("Graph Resource (/v1.0/*)", style="bold cyan", width=26)
    t.add_column("Object Count", justify="right", width=14)
    t.add_column("Sample / Status", style="white", overflow="fold")

    for r in results:
        if r.get("error"):
            t.add_row(r["resource"], "[red]ERR[/red]", f"[red]{r['error']}[/red]")
        else:
            names = [
                item.get("displayName") or item.get("userPrincipalName") or item.get("appId") or item.get("id")
                for item in (r.get("sample") or [])
            ]
            t.add_row(r["resource"], f"[bold green]{r.get('count', 0)}[/bold green]", ", ".join(str(n) for n in names if n) or "[dim]Empty[/dim]")
    console.print(t)
    console.print(
        Panel(
            f"ROADrecon CLI binary detected: {'[bold green]' + road_bin + '[/bold green]' if road_bin else '[dim]Not installed (pip install roadrecon)[/dim]'}",
            title="[bold cyan]ROADtools Integration Status[/bold cyan]",
            border_style="cyan",
        )
    )


# ---------------------------------------------------------------------------
# PyWebView Full Desktop GUI (All Python + Rich HTML Export)
# ---------------------------------------------------------------------------

GUI_HTML_TEMPLATE = """<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <title>M365 Canary & Tenant Recon Suite</title>
  <style>
    * { box-sizing: border-box; }
    body { font-family: 'Segoe UI', system-ui, sans-serif; margin: 0; background: #1e1e2e; color: #cdd6f4; }
    #header { background: #181825; padding: 12px 20px; border-bottom: 1px solid #313244;
              display: flex; justify-content: space-between; align-items: center; }
    #header h1 { margin: 0; font-size: 16px; color: #89b4fa; letter-spacing: 0.3px; }
    .input-bar { padding: 12px 20px 6px; display: flex; gap: 8px; flex-wrap: wrap; }
    .input-bar input { flex: 1; min-width: 220px; padding: 10px 12px; background: #313244; border: 1px solid #45475a;
                       border-radius: 6px; color: #cdd6f4; font-size: 13px; outline: none; }
    .input-bar input:focus { border-color: #89b4fa; }
    .input-bar select { padding: 10px 12px; background: #313244; border: 1px solid #45475a;
                        border-radius: 6px; color: #cdd6f4; font-size: 13px; }
    .input-bar button { padding: 10px 20px; background: #89b4fa; border: none;
                        border-radius: 6px; color: #11111b; font-weight: bold; cursor: pointer; }
    .input-bar button:hover { background: #b4befe; }
    #menu { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; padding: 8px 20px 12px; }
    .menu-btn { padding: 11px 13px; background: #313244; border: 1px solid #45475a; border-radius: 6px;
                color: #cdd6f4; cursor: pointer; text-align: left; font-size: 12.5px; transition: all 0.12s; }
    .menu-btn:hover { background: #45475a; border-color: #89b4fa; transform: translateY(-1px); }
    .menu-btn .num { color: #f9e2af; font-weight: bold; margin-right: 6px; }
    #status { padding: 2px 20px 6px; color: #a6e3a1; font-size: 12px; min-height: 20px; font-weight: 500; }
    #output { margin: 0 20px 20px; background: #11111b; border: 1px solid #313244;
              border-radius: 6px; padding: 16px; height: calc(100vh - 330px); min-height: 360px; overflow: auto;
              font-family: 'Cascadia Code', 'Consolas', monospace; font-size: 12px; }
    #output pre { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: inherit; }
  </style>
</head>
<body>
  <div id="header">
    <h1>🛡️ M365 Canary & Deep Tenant Recon Suite (Desktop GUI)</h1>
    <span style="font-size:12px; color:#a6adc8;">PyWebView + Rich HTML Bridge | Self-Healing Scheduled Tasks</span>
  </div>
  <div class="input-bar">
    <input id="target-input" placeholder="Primary Target: domain, email, or comma-separated domains (e.g. contoso.com)..." onkeydown="if(event.key==='Enter')submitTarget()" />
    <input id="aux-input" placeholder="Optional Aux Input: emails for [11], vectors (1,2,6 or all) for [7], task action (h/r/l/d) for [8], or Bearer token for [14]..." />
    <select id="enum-method" title="Method for [11] Silent User Enumeration">
      <option value="onedrive">[11] OneDriveEnum (Zero Audit)</option>
      <option value="sso">[11] SeamlessSSO Autologon (Zero Audit)</option>
      <option value="gct">[11] GetCredentialType (o365creeper)</option>
    </select>
    <button onclick="submitTarget()">Deep Scan [1]</button>
  </div>
  <div id="status">Ready. Enter a target domain above and click Deep Scan [1] or any action tile below.</div>
  <div id="menu">
    <div class="menu-btn" onclick="runMenu('1')"><span class="num">[1]</span>Deep-scan domain / email</div>
    <div class="menu-btn" onclick="runMenu('2')"><span class="num">[2]</span>Batch scan (file / list)</div>
    <div class="menu-btn" onclick="runMenu('3')"><span class="num">[3]</span>Inspect raw CSS / JSON</div>
    <div class="menu-btn" onclick="runMenu('4')"><span class="num">[4]</span>Session comparison table</div>
    <div class="menu-btn" onclick="runMenu('5')"><span class="num">[5]</span>Static trigger vectors</div>
    <div class="menu-btn" onclick="runMenu('6')"><span class="num">[6]</span>Export JSON report</div>
    <div class="menu-btn" onclick="runMenu('7')"><span class="num">[7]</span>🔥 Active trigger (fire)</div>
    <div class="menu-btn" onclick="runMenu('8')"><span class="num">[8]</span>🛠️ Task & Auto-Heal Hub</div>
    <div class="menu-btn" onclick="runMenu('9')"><span class="num">[9]</span>⏱️ Install 10-min self-heal task</div>
    <div class="menu-btn" onclick="runMenu('10')"><span class="num">[10]</span>Full tenant recon (MSFTRecon)</div>
    <div class="menu-btn" onclick="runMenu('11')"><span class="num">[11]</span>Silent user enumeration</div>
    <div class="menu-btn" onclick="runMenu('12')"><span class="num">[12]</span>Endpoint & Azure DNS discovery</div>
    <div class="menu-btn" onclick="runMenu('13')"><span class="num">[13]</span>OPSEC detection risk matrix</div>
    <div class="menu-btn" onclick="runMenu('14')"><span class="num">[14]</span>Authenticated Graph enum</div>
    <div class="menu-btn" onclick="runMenu('k')" style="border-color:#f38ba8;"><span class="num">[K]</span>🔔 Knock Mode (SIEM Knock)</div>
    <div class="menu-btn" onclick="clearOutput()"><span class="num">[C]</span>Clear output pane</div>
    <div class="menu-btn" onclick="quitApp()"><span class="num">[Q]</span>Quit application</div>
  </div>
  <div id="output"><em style="color:#6c7086;">Ready. Enter a domain (e.g. microsoft.com or lifestylesolutions.org.au) and click Deep Scan [1], [K] for Knock Mode, or [8] to inspect & auto-heal your scheduled tasks.</em></div>
  <script>
    function getInputs() {
      return {
        target: document.getElementById('target-input').value.trim(),
        aux: document.getElementById('aux-input').value.trim(),
        method: document.getElementById('enum-method').value
      };
    }
    function submitTarget() {
      const inp = getInputs();
      if (!inp.target) {
        document.getElementById('status').textContent = "Please enter a domain or email in the Primary Target box first.";
        return;
      }
      document.getElementById('status').textContent = "Running 6-stage Deep Scan on " + inp.target + "...";
      pywebview.api.run_scan_gui(inp.target).then(() => {
        document.getElementById('status').textContent = "Scan complete for " + inp.target + ".";
      });
    }
    function runMenu(n) {
      const inp = getInputs();
      document.getElementById('status').textContent = "Executing action [" + n.toUpperCase() + "]...";
      pywebview.api.run_menu_gui(n, inp.target, inp.aux, inp.method).then(() => {
        document.getElementById('status').textContent = "Action [" + n.toUpperCase() + "] complete.";
      });
    }
    function clearOutput() {
      document.getElementById('output').innerHTML = "";
      document.getElementById('status').textContent = "Output cleared.";
    }
    function quitApp() {
      pywebview.api.quit_app();
    }
    function setOutput(html) {
      const el = document.getElementById('output');
      el.innerHTML = html;
      el.scrollTop = 0;
    }
  </script>
</body>
</html>
"""


class GuiApi:
    """Exposed to JavaScript via pywebview's JS bridge — supports all actions [1]–[14] and [K]."""

    def __init__(self, window_ref: list):
        self._window_ref = window_ref

    def _push_output(self):
        if not _gui_console:
            return
        html = _gui_console.export_html()
        if self._window_ref and self._window_ref[0]:
            self._window_ref[0].evaluate_js(f"setOutput({json.dumps(html)})")

    def run_scan_gui(self, target: str):
        global _last_result, _history
        if not target:
            return
        _gui_console.clear()
        res = run_scan(target)
        _history.append(res)
        _last_result = res
        render_scan_result(res, show_css=False)
        if res.has_canary:
            render_trigger_vector_inspector(res)
        self._push_output()

    def run_menu_gui(self, choice: str, target: str, aux: str = "", method: str = "onedrive"):
        global _last_result, _history, _last_trigger_context, _trigger_history
        _gui_console.clear()
        active_domain = target or (_last_result.domain if _last_result else "")

        if choice == "1":
            if not active_domain:
                console.print("[yellow]Enter a domain or email in the Primary Target input bar first.[/yellow]")
            else:
                res = run_scan(active_domain)
                _history.append(res)
                _last_result = res
                render_scan_result(res, show_css=False)
                if res.has_canary:
                    render_trigger_vector_inspector(res)

        elif choice == "2":
            raw_source = target or aux
            if not raw_source:
                console.print("[yellow]Enter a file path or comma-separated domains in the Primary Target bar.[/yellow]")
            else:
                p = Path(raw_source)
                if p.exists() and p.is_file():
                    domains = [ln.strip() for ln in p.read_text(encoding="utf-8").splitlines() if ln.strip() and not ln.strip().startswith("#")]
                else:
                    domains = [d.strip() for d in raw_source.split(",") if d.strip()]
                for d in domains:
                    res = run_scan(d)
                    _history.append(res)
                    _last_result = res
                    render_scan_result(res, show_css=False)
                render_history_table(_history)

        elif choice == "3":
            if not _last_result:
                console.print("[yellow]Scan a domain first using [1].[/yellow]")
            else:
                if _last_result.co_tenant_domains:
                    console.print(
                        Panel(
                            ", ".join(_last_result.co_tenant_domains),
                            title=f"[bold green]All {len(_last_result.co_tenant_domains)} Co-Tenant Sister Domains — {_last_result.domain}[/bold green]",
                            border_style="green",
                        )
                    )
                if _last_result.css_content:
                    console.print(
                        Panel(
                            Syntax(_last_result.css_content, "css", theme="monokai", line_numbers=True, word_wrap=True),
                            title=f"[bold magenta]Raw Custom CSS — {_last_result.domain}[/bold magenta]",
                            border_style="magenta",
                        )
                    )
                if _last_result.raw_cred_response:
                    console.print(
                        Panel(
                            Syntax(json.dumps(_last_result.raw_cred_response, indent=2), "json", theme="monokai", word_wrap=True),
                            title=f"[bold cyan]GetCredentialType JSON — {_last_result.domain}[/bold cyan]",
                            border_style="cyan",
                        )
                    )

        elif choice == "4":
            render_history_table(_history)

        elif choice == "5":
            if _last_result:
                render_trigger_vector_inspector(_last_result)
            else:
                console.print("[yellow]Scan a domain first using [1].[/yellow]")

        elif choice == "6":
            out = Path(aux.strip() if aux.strip().endswith(".json") else "m365_deep_recon_report.json")
            payload = [r.__dict__ for r in _history]
            out.write_text(json.dumps(payload, indent=2, default=str), encoding="utf-8")
            console.print(Panel(f"[bold green]✓ Exported {len(payload)} scan record(s) to {out.resolve()}[/bold green]", title="Export Complete", border_style="green"))

        elif choice == "7":
            urls = []
            dom_label = active_domain or "custom-target"
            if target and (target.startswith("http://") or target.startswith("https://")):
                urls = [target]
            elif _last_result and _last_result.canary_urls:
                urls = list(_last_result.canary_urls)
                dom_label = _last_result.domain
            elif active_domain and is_valid_domain(active_domain):
                res = run_scan(active_domain)
                _history.append(res)
                _last_result = res
                urls = list(res.canary_urls)
                dom_label = res.domain

            if not urls:
                console.print("[yellow]No Canary URLs found on target. Scan a domain with an active Canary token or paste a URL in Primary Target.[/yellow]")
            else:
                vec_raw = aux.strip().lower() or "all"
                if vec_raw in ("a", "all"):
                    keys = list(TRIGGER_VECTOR_PRESETS.keys())
                else:
                    keys = [k.strip() for k in vec_raw.split(",") if k.strip() in TRIGGER_VECTOR_PRESETS] or ["1"]
                attempts = fire_trigger_vectors(urls, keys)
                _trigger_history.extend(attempts)
                _last_trigger_context = {
                    "domain": dom_label,
                    "css_url": _last_result.css_url if _last_result else None,
                    "urls": urls,
                    "vectors": keys,
                    "headers": {},
                    "captured_at": datetime.now(timezone.utc).isoformat(),
                }
                render_trigger_attempts(attempts, dom_label)

        elif choice.lower() == "k":
            urls = []
            dom_label = active_domain or "custom-target"
            if target and (target.startswith("http://") or target.startswith("https://")):
                urls = [target]
            elif _last_result and _last_result.canary_urls:
                urls = list(_last_result.canary_urls)
                dom_label = _last_result.domain
            elif active_domain and is_valid_domain(active_domain):
                res = run_scan(active_domain)
                _history.append(res)
                _last_result = res
                urls = list(res.canary_urls)
                dom_label = res.domain

            profile = ReconProfile(
                domain=dom_label,
                knock_mode=True,
                tool_signature_ua="TeamFiltration/3.5.0",
                include_signin_knock=bool(dom_label and is_valid_domain(dom_label)),
                include_onedrive_knock=bool(dom_label and is_valid_domain(dom_label)),
                include_graph_knock=bool(aux.strip() and len(aux.strip()) > 30),
                burst_count=25,
            )
            knock_res = run_knock_mode(
                profile=profile,
                urls=urls,
                preset_keys=list(TRIGGER_VECTOR_PRESETS.keys()),
                tenant_slug=_last_result.tenant_slug if _last_result else None,
                graph_token=aux.strip() if len(aux.strip()) > 30 else None,
            )
            attempts = knock_res.get("canary_attempts") or []
            _trigger_history.extend(attempts)
            if urls:
                _last_trigger_context = {
                    "domain": dom_label,
                    "css_url": _last_result.css_url if _last_result else None,
                    "urls": urls,
                    "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                    "headers": {},
                    "captured_at": datetime.now(timezone.utc).isoformat(),
                }
            render_knock_summary(knock_res)

        elif choice == "8":
            tasks_root = Path.home() / "M365CanaryTasks"
            tasks_root.mkdir(parents=True, exist_ok=True)
            task_dirs = sorted([d for d in tasks_root.iterdir() if d.is_dir() and (d / "m365_canary_recurring.json").exists()])
            if not task_dirs:
                console.print(Panel(f"[yellow]No scheduled tasks found in {tasks_root}[/yellow]", title="[8] Scheduled Task Hub", border_style="yellow"))
            else:
                sub_cmd = aux.strip().lower()
                for tdir in task_dirs:
                    if sub_cmd in ("", "h", "heal"):
                        heal_res = heal_task_state_now(tdir)
                        status_note = "ROTATED & HEALED" if heal_res.get("rotated") else "Up-to-date (Self-Heal Active)"
                    elif sub_cmd in ("r", "run"):
                        subprocess.run(["schtasks", "/Run", "/TN", tdir.name], capture_output=True, text=True, shell=False)
                        status_note = "Triggered immediate run (schtasks /Run)"
                    else:
                        status_note = "Inspected"

                table = Table(title=f"[8] Installed Self-Healing Scheduled Tasks ({tasks_root})", box=box.HEAVY_HEAD, border_style="cyan", expand=True)
                table.add_column("#", style="cyan", width=3)
                table.add_column("Task Name", style="bold white")
                table.add_column("Domain", style="green")
                table.add_column("Scheduler Status")
                table.add_column("Heals", justify="right", style="magenta")
                table.add_column("Active Base Canary URL(s)", style="yellow", overflow="fold")
                table.add_column("Last Tick Log", style="dim white", overflow="fold")

                for idx, tdir in enumerate(task_dirs, start=1):
                    st = json.loads((tdir / "m365_canary_recurring.json").read_text(encoding="utf-8"))
                    lg_path = tdir / "m365_canary_recurring.log"
                    last_ln = "-"
                    if lg_path.exists():
                        lns = [x.strip() for x in lg_path.read_text(encoding="utf-8", errors="replace").splitlines() if x.strip()]
                        if lns:
                            last_ln = lns[-1][-90:]
                    table.add_row(
                        str(idx),
                        tdir.name,
                        st.get("domain") or "?",
                        _query_schtasks_status(tdir.name),
                        str(st.get("heal_count") or 0),
                        "\n".join(st.get("base_urls") or []),
                        last_ln,
                    )
                console.print(table)
                console.print("[dim]Tip: Enter 'h' in Aux Input to force auto-heal all tasks, or 'r' to run all tasks immediately.[/dim]")

        elif choice == "9":
            ctx = _last_trigger_context
            if not ctx and _last_result and _last_result.canary_urls:
                ctx = {
                    "domain": _last_result.domain,
                    "css_url": _last_result.css_url,
                    "urls": list(_last_result.canary_urls),
                    "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                    "headers": {},
                    "captured_at": datetime.now(timezone.utc).isoformat(),
                }
            elif not ctx and active_domain and is_valid_domain(active_domain):
                res = run_scan(active_domain)
                _history.append(res)
                _last_result = res
                if res.canary_urls:
                    ctx = {
                        "domain": res.domain,
                        "css_url": res.css_url,
                        "urls": list(res.canary_urls),
                        "vectors": list(TRIGGER_VECTOR_PRESETS.keys()),
                        "headers": {},
                        "captured_at": datetime.now(timezone.utc).isoformat(),
                    }
            if not ctx or not ctx.get("urls"):
                console.print("[yellow]Scan a domain with an active Canary token first so [9] can install the self-healing recurring task.[/yellow]")
            else:
                task_name = f"M365CanaryRecurring_{re.sub(r'[^A-Za-z0-9_-]', '_', ctx['domain'])}"
                work_dir = Path.home() / "M365CanaryTasks" / task_name
                urls_pool = _randomize_urls(ctx["urls"], count=max(20, 5 * len(ctx["urls"])))
                info = create_recurring_task(ctx, task_name, work_dir, urls_pool, interval_minutes=10, auto_heal=True, randomize_pool=True)
                console.print(
                    Panel(
                        f"[bold green]✓ Self-Healing 10-Min Scheduled Task Installed[/bold green]\n"
                        f"Task Name : [cyan]{info['task_name']}[/cyan]\n"
                        f"Domain    : [white]{ctx['domain']}[/white]\n"
                        f"Base URLs : {', '.join(ctx['urls'])}\n"
                        f"State File: [cyan]{info['state_path']}[/cyan]\n"
                        f"PS1 Worker: [cyan]{info['ps1_path']}[/cyan]",
                        title="[9] Recurring Task Installed",
                        border_style="green",
                    )
                )

        elif choice == "10":
            if not active_domain:
                console.print("[yellow]Enter a domain in Primary Target first.[/yellow]")
            else:
                recon = run_full_tenant_recon(active_domain)
                render_tenant_recon(recon)

        elif choice == "11":
            if not active_domain:
                console.print("[yellow]Enter a target domain in Primary Target and comma-separated emails in Aux Input.[/yellow]")
            else:
                clean_dom = active_domain.split("@")[-1]
                users = [u.strip() for u in (aux or active_domain).split(",") if "@" in u]
                if not users:
                    users = [f"admin@{clean_dom}", f"info@{clean_dom}", f"canarycheck@{clean_dom}"]
                slug = _last_result.tenant_slug if (_last_result and _last_result.domain == clean_dom) else None
                if not slug:
                    soap = get_federation_domains_soap(clean_dom)
                    for d in soap.get("domains", []):
                        if d.endswith(".onmicrosoft.com") and not d.endswith(".mail.onmicrosoft.com"):
                            slug = d[: -len(".onmicrosoft.com")]
                            break
                if method == "onedrive":
                    if not slug:
                        console.print("[yellow]Could not determine tenant .onmicrosoft.com slug for OneDriveEnum; falling back to GetCredentialType.[/yellow]")
                        enum_res = enum_user_getcredentialtype(users)
                    else:
                        enum_res = enum_user_onedrive(slug, users)
                elif method == "sso":
                    enum_res = enum_user_seamless_sso(clean_dom, users)
                else:
                    enum_res = enum_user_getcredentialtype(users)
                render_user_enum(enum_res, clean_dom)

        elif choice == "12":
            if not active_domain:
                console.print("[yellow]Enter a domain in Primary Target first.[/yellow]")
            else:
                clean_dom = active_domain.split("@")[-1]
                slug = _last_result.tenant_slug if (_last_result and _last_result.domain == clean_dom) else None
                findings = probe_endpoints(clean_dom, slug)
                render_endpoint_findings(findings, clean_dom)

        elif choice == "13":
            render_risk_matrix()

        elif choice == "14":
            token = aux.strip()
            if not token:
                console.print(
                    Panel(
                        "[yellow]Paste a Microsoft Graph Bearer token into the Aux Input bar and click [14] again.[/yellow]\n"
                        f"ROADrecon CLI status: {shutil.which('roadrecon') or 'Not installed (pip install roadrecon)'}",
                        title="[14] Authenticated Graph Enumeration",
                        border_style="yellow",
                    )
                )
            else:
                g_res = run_graph_suite(token)
                render_graph_enumeration(g_res)

        self._push_output()

    def quit_app(self):
        if self._window_ref and self._window_ref[0]:
            self._window_ref[0].destroy()


def launch_gui(initial_target: Optional[str] = None):
    """Entry point for --gui mode. Opens a native desktop window via PyWebView."""
    global _GUI_MODE, _gui_console
    import webview

    _GUI_MODE = True
    _gui_console = HtmlConsole()

    window_ref = [None]
    api = GuiApi(window_ref)

    window = webview.create_window(
        "M365 Canary & Deep Tenant Recon Suite",
        html=GUI_HTML_TEMPLATE,
        js_api=api,
        width=1360,
        height=900,
        resizable=True,
    )
    window_ref[0] = window

    def _on_loaded():
        if initial_target:
            window.evaluate_js(f"document.getElementById('target-input').value = {json.dumps(initial_target)}; submitTarget();")
        elif _last_result:
            _gui_console.clear()
            render_scan_result(_last_result, show_css=False)
            api._push_output()

    window.events.loaded += _on_loaded
    webview.start(debug=False)
    _GUI_MODE = False


def interactive_tui(initial_results: List[ScanResult]):
    global _last_result, _history, _last_trigger_context, _trigger_history
    history: List[ScanResult] = list(initial_results)
    last_result: Optional[ScanResult] = history[-1] if history else None
    trigger_history: List[TriggerAttempt] = []
    last_trigger_context: Optional[dict] = None
    _history = history
    _last_result = last_result

    console.print()
    console.print(
        Panel(
            "[bold white]M365 / Entra ID Deep Tenant OSINT, 20-Tool Recon & Self-Healing Canary Suite[/bold white]\n"
            "[dim]Includes PyWebView Desktop GUI (--gui or [g]), Knock Mode [K], Co-Tenant SOAP Recon, MSFTRecon/AADInternals [10], "
            "Silent User Enum [11], Endpoint Discovery [12], OPSEC Risk Matrix [13], Graph Audit [14], & Self-Healing Tasks [8]/[9].[/dim]",
            title="[bold bright_blue]🛡️  M365 Deep Recon & Canary TUI / GUI[/bold bright_blue]",
            border_style="bright_blue",
        )
    )

    while True:
        console.print()
        console.print(Rule("[bold cyan]TUI Menu[/bold cyan]", style="cyan"))
        console.print(
            "  [bold cyan][1][/bold cyan]   Deep-scan a domain or email (Entra + Sister Domains + EXO/SPO/Teams/MDI + Canary)\n"
            "  [bold cyan][2][/bold cyan]   Batch scan domains from a file or comma-separated list\n"
            "  [bold cyan][3][/bold cyan]   Inspect raw Custom CSS, Co-Tenant Domains & GetCredentialType JSON\n"
            "  [bold cyan][4][/bold cyan]   View session comparison table\n"
            "  [bold cyan][5][/bold cyan]   Inspect Canary Trigger Vectors & CSS Selector Bindings (Static Analysis)\n"
            "  [bold cyan][6][/bold cyan]   Export findings (JSON / HTML Assessment Report)\n"
            "  [bold cyan][7][/bold cyan]   🔥 Trigger selected vectors against a canary URL\n"
            "  [bold red][K][/bold red]   🔔 Knock Mode — fire all detected canaries with loud UAs, max detection likelihood\n"
            "  [bold cyan][8][/bold cyan]   🛠️  Manage, Auto-Heal, Run & Inspect Scheduled Tasks (M365CanaryTasks Hub)\n"
            "  [bold cyan][9][/bold cyan]   ⏱️  Install self-healing recurring 10-min scheduled task\n"
            "  [bold magenta][P][/bold magenta]   Profile Manager (Custom settings, rate limits, output modes)\n"
            "  [bold cyan][10][/bold cyan]  Tenant Profile Review (OIDC Discovery + Realm + Verified Domains)\n"
            "  [bold cyan][11][/bold cyan]  Account Presence Review (OneDrive personal-site + GetCredentialType)\n"
            "  [bold cyan][12][/bold cyan]  Endpoint Discovery (Autodiscover, EWS, SharePoint, Lync, Azure PaaS, Dynamics)\n"
            "  [bold cyan][13][/bold cyan]  Detection Coverage Reference (Telemetry & Audit Log Risk Matrix)\n"
            "  [bold cyan][14][/bold cyan]  Authenticated Directory Review (ROADrecon / AzureHound / Graph API)\n"
            "  [bold green][15][/bold green]  Internal Network Discovery (Two-Phase Nmap Service & Port Inventory)\n"
            "  [bold green][16][/bold green]  Internal DNS Review (SRV Records, Reverse Lookups & Zone Transfer Review)\n"
            "  [bold green][17][/bold green]  Internal Directory Review (Read-only LDAP Groups, Users, Computers, Trusts)\n"
            "  [bold green][18][/bold green]  Internal Share Inventory (Read-only SMB Share & Access Listing)\n"
            "  [bold green][19][/bold green]  Internal Posture Audit (SPN Inventory, Pre-Auth Review, SYSVOL Scan, Templates)\n"
            "  [bold green][20][/bold green]  Internal Path Mapping (BloodHound Read-Only Relationship Inventory)\n"
            "  [bold cyan][g][/bold cyan]   🖥️  Launch Full Desktop GUI Window (PyWebView)\n"
            "  [bold cyan][q][/bold cyan]   Quit"
        )

        choice = Prompt.ask(
            "\n[bold bright_blue]Choose an action[/bold bright_blue]",
            choices=["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "p", "P", "k", "K", "g", "q"],
            default="1",
        )

        if choice == "q":
            break
        elif choice == "g":
            launch_gui(last_result.domain if last_result else None)
        elif choice.lower() == "k":
            trigger_history, ctx = interactive_knock_menu(last_result, trigger_history)
            if ctx:
                last_trigger_context = ctx
                _last_trigger_context = ctx
        elif choice == "1":
            target = Prompt.ask("[bold green]Enter domain or email[/bold green]").strip()
            if not target:
                continue
            res = run_scan(target)
            history.append(res)
            last_result = res
            _last_result = res
            render_scan_result(res, show_css=False)
            if res.has_canary:
                render_trigger_vector_inspector(res)
        elif choice == "2":
            raw_source = Prompt.ask("[bold green]Enter path to domains file or comma-separated domains[/bold green]").strip()
            if not raw_source:
                continue
            path_candidate = Path(raw_source)
            if path_candidate.exists() and path_candidate.is_file():
                domains = [
                    line.strip()
                    for line in path_candidate.read_text(encoding="utf-8").splitlines()
                    if line.strip() and not line.strip().startswith("#")
                ]
            else:
                domains = [d.strip() for d in raw_source.split(",") if d.strip()]
            for d in domains:
                res = run_scan(d)
                history.append(res)
                last_result = res
                _last_result = res
                render_scan_result(res, show_css=False)
            render_history_table(history)
        elif choice == "3":
            if not last_result:
                console.print("[yellow]No domain scanned yet.[/yellow]")
                continue
            if last_result.co_tenant_domains:
                console.print(
                    Panel(
                        ", ".join(last_result.co_tenant_domains),
                        title=f"[bold green]All {len(last_result.co_tenant_domains)} Co-Tenant Domains — {last_result.domain}[/bold green]",
                        border_style="green",
                    )
                )
            if last_result.css_content:
                console.print(
                    Panel(
                        Syntax(last_result.css_content, "css", theme="monokai", line_numbers=True, word_wrap=True),
                        title=f"[bold magenta]Raw Custom CSS — {last_result.domain}[/bold magenta]",
                        border_style="magenta",
                    )
                )
            if last_result.raw_cred_response and Confirm.ask("View raw GetCredentialType JSON?", default=False):
                console.print(
                    Panel(
                        Syntax(json.dumps(last_result.raw_cred_response, indent=2), "json", theme="monokai", word_wrap=True),
                        title=f"[bold cyan]GetCredentialType JSON — {last_result.domain}[/bold cyan]",
                        border_style="cyan",
                    )
                )
        elif choice == "4":
            render_history_table(history)
        elif choice == "5":
            if last_result:
                render_trigger_vector_inspector(last_result)
        elif choice == "6":
            if not history and not trigger_history:
                console.print("[yellow]No assessment data available to export.[/yellow]")
                continue
            fmt = Prompt.ask("Select export format", choices=["json", "html", "both"], default="both")
            payload = {"scan_history": [r.__dict__ for r in history]}
            if trigger_history:
                payload["trigger_history"] = [t.__dict__ for t in trigger_history]
            if fmt in ("json", "both"):
                out_json = Prompt.ask("Output JSON file path", default="m365_assessment_report.json")
                Path(out_json).write_text(json.dumps(payload, indent=2, default=str), encoding="utf-8")
                console.print(f"[bold green]✓ Exported JSON to {out_json}[/bold green]")
            if fmt in ("html", "both"):
                out_html = Prompt.ask("Output HTML file path", default="m365_assessment_report.html")
                export_html_report(payload, out_html)
        elif choice == "7":
            trigger_history, ctx = interactive_trigger_menu(last_result, trigger_history)
            if ctx:
                last_trigger_context = ctx
                _last_trigger_context = ctx
        elif choice == "8":
            interactive_manage_tasks_menu()
        elif choice == "9":
            interactive_recurring_menu(last_trigger_context, last_result)
        elif choice == "10":
            dom = Prompt.ask("[bold green]Enter domain for Full Tenant Recon[/bold green]", default=last_result.domain if last_result else "").strip()
            if dom:
                recon = run_full_tenant_recon(dom)
                render_tenant_recon(recon)
        elif choice == "11":
            dom = Prompt.ask("[bold green]Enter target domain[/bold green]", default=last_result.domain if last_result else "").strip()
            if not dom:
                continue
            raw_users = Prompt.ask("[bold green]Enter path to email list file or comma-separated emails[/bold green]").strip()
            if not raw_users:
                continue
            upath = Path(raw_users)
            if upath.exists() and upath.is_file():
                users = [ln.strip() for ln in upath.read_text(encoding="utf-8").splitlines() if ln.strip() and "@" in ln]
            else:
                users = [u.strip() for u in raw_users.split(",") if "@" in u]
            m_choice = Prompt.ask("Method ([1] OneDriveEnum Zero-Audit, [2] SeamlessSSO Zero-Audit, [3] GetCredentialType)", choices=["1", "2", "3"], default="1")
            if m_choice == "1":
                slug = last_result.tenant_slug if (last_result and last_result.domain == dom) else None
                if not slug:
                    soap = get_federation_domains_soap(dom)
                    for d in soap.get("domains", []):
                        if d.endswith(".onmicrosoft.com") and not d.endswith(".mail.onmicrosoft.com"):
                            slug = d[: -len(".onmicrosoft.com")]
                            break
                if not slug:
                    slug = Prompt.ask("Enter tenant .onmicrosoft.com slug (e.g. contoso)").strip()
                res_enum = enum_user_onedrive(slug, users)
            elif m_choice == "2":
                res_enum = enum_user_seamless_sso(dom, users)
            else:
                res_enum = enum_user_getcredentialtype(users)
            render_user_enum(res_enum, dom)
        elif choice == "12":
            dom = Prompt.ask("[bold green]Enter target domain for Endpoint Discovery[/bold green]", default=last_result.domain if last_result else "").strip()
            if dom:
                slug = last_result.tenant_slug if (last_result and last_result.domain == dom) else None
                with console.status(f"[bold cyan][12] Probing Autodiscover, EWS, SharePoint, Lync & Azure DNS for {dom}...", spinner="dots"):
                    findings = probe_endpoints(dom, slug)
                render_endpoint_findings(findings, dom)
        elif choice == "13":
            render_risk_matrix()
        elif choice == "14":
            sub_action = Prompt.ask("Action ([1] Graph API Resource Enum, [2] ROADrecon Directory Inventory, [3] AzureHound Collector)", choices=["1", "2", "3"], default="1")
            if sub_action == "1":
                token = Prompt.ask("[bold green]Paste Microsoft Graph Bearer Token[/bold green]").strip()
                if token:
                    with console.status("[bold cyan][14] Querying Microsoft Graph v1.0 directory resources...", spinner="dots"):
                        g_res = run_graph_suite(token)
                    render_graph_enumeration(g_res)
            elif sub_action == "2":
                token = Prompt.ask("[bold green]Paste Bearer Token for ROADrecon[/bold green]").strip()
                if token:
                    res = run_roadrecon(token)
                    console.print(res.get("stdout") or res.get("error") or "Completed.")
            elif sub_action == "3":
                t_id = Prompt.ask("Tenant Domain / ID", default=last_result.domain if last_result else "").strip()
                res = run_azurehound(tenant=t_id)
                console.print(res.get("stdout") or res.get("error") or "Completed.")
        elif choice.lower() == "p":
            interactive_profile_manager()
        elif choice == "15":
            eid = get_session_engagement_id()
            subnet = Prompt.ask("[bold green]Enter subnet for Internal Network Discovery (e.g. 192.168.1.0/24)[/bold green]").strip()
            if subnet:
                console.print(f"[dim]Telemetry Notice: Nmap discovery generates network connection logs and firewall alerts on target hosts.[/dim]")
                if Confirm.ask(f"Confirm execution of read-only network discovery on {subnet} under engagement {eid}?", default=True):
                    with console.status(f"[bold green]Running two-phase Nmap port & service discovery on {subnet}...", spinner="dots"):
                        res = internal_nmap_discover(subnet)
                    render_internal_results(res, f"Network Discovery — {subnet}")
        elif choice == "16":
            eid = get_session_engagement_id()
            dns_ip = Prompt.ask("[bold green]Enter Internal DNS Server IP[/bold green]").strip()
            target_dom = Prompt.ask("[bold green]Enter Internal Domain Name[/bold green]", default=last_result.domain if last_result else "").strip()
            if dns_ip and target_dom:
                console.print(f"[dim]Telemetry Notice: Standard DNS SRV queries and AXFR attempt logged under DNS Server audit logs.[/dim]")
                if Confirm.ask(f"Confirm execution of internal DNS review against {dns_ip}?", default=True):
                    with console.status("[bold green]Querying SRV records, reverse lookups, and zone transfer posture...", spinner="dots"):
                        res = internal_dns_enum(dns_ip, target_dom)
                    render_internal_results(res, f"Internal DNS Review — {target_dom}")
        elif choice == "17":
            eid = get_session_engagement_id()
            dc_host = Prompt.ask("[bold green]Enter Domain Controller Host / IP[/bold green]").strip()
            dom = Prompt.ask("[bold green]Enter Domain Name[/bold green]", default=last_result.domain if last_result else "").strip()
            u = Prompt.ask("Username for LDAP query (Optional, Enter to skip)", default="").strip() or None
            p = Prompt.ask("Password (Optional, Enter to skip)", default="").strip() or None
            if dc_host and dom:
                console.print(f"[dim]Telemetry Notice: Read-only LDAP queries logged as directory service access (Event 4662) on DC.[/dim]")
                if Confirm.ask(f"Confirm read-only directory enumeration against {dc_host} under engagement {eid}?", default=True):
                    with console.status("[bold green]Executing read-only LDAP directory object review...", spinner="dots"):
                        res = internal_directory_enum(dc_host, dom, u, p)
                    render_internal_results(res, f"Directory Inventory — {dom}")
        elif choice == "18":
            eid = get_session_engagement_id()
            host = Prompt.ask("[bold green]Enter Target Host / Subnet for Share Inventory[/bold green]").strip()
            u = Prompt.ask("Username for SMB review (Optional)", default="").strip() or None
            p = Prompt.ask("Password (Optional)", default="").strip() or None
            if host:
                console.print(f"[dim]Telemetry Notice: SMB share listing recorded under File Server Event 5140.[/dim]")
                if Confirm.ask(f"Confirm read-only SMB share review on {host}?", default=True):
                    with console.status("[bold green]Listing network shares and access permissions...", spinner="dots"):
                        res = internal_share_inventory(host, u, p)
                    render_internal_results(res, f"Share Inventory — {host}")
        elif choice == "19":
            eid = get_session_engagement_id()
            dc_host = Prompt.ask("[bold green]Enter Domain Controller Host / IP[/bold green]").strip()
            dom = Prompt.ask("[bold green]Enter Domain Name[/bold green]", default=last_result.domain if last_result else "").strip()
            u = Prompt.ask("Username (Optional)", default="").strip() or None
            p = Prompt.ask("Password (Optional)", default="").strip() or None
            if dc_host and dom:
                console.print(f"[dim]Telemetry Notice: Evaluates SPN inventory, pre-auth configuration, SYSVOL GPP file presence, and certificate templates.[/dim]")
                if Confirm.ask(f"Confirm execution of read-only internal posture audit on {dc_host} under engagement {eid}?", default=True):
                    with console.status("[bold green]Executing posture audit modules...", spinner="dots"):
                        spn_res = internal_spn_inventory(dc_host, dom, u, p)
                        preauth_res = internal_preauth_inventory(dc_host, dom, u, p)
                        sysvol_res = internal_sysvol_cpassword_scan(dc_host, u, p)
                        cert_res = internal_certificate_template_audit(dc_host, dom, u, p)
                    combined = {
                        "spn_inventory": spn_res,
                        "preauth_inventory": preauth_res,
                        "sysvol_cpassword_review": sysvol_res,
                        "certificate_template_audit": cert_res,
                    }
                    render_internal_results(combined, f"Internal Posture Audit — {dom}")
        elif choice == "20":
            eid = get_session_engagement_id()
            dom = Prompt.ask("[bold green]Enter Domain Name for BloodHound Path Mapping[/bold green]", default=last_result.domain if last_result else "").strip()
            u = Prompt.ask("Domain Username", default="").strip() or None
            p = Prompt.ask("Domain Password", default="").strip() or None
            if dom:
                console.print(f"[dim]Telemetry Notice: BloodHound collector executes standard LDAP and RPC session queries.[/dim]")
                if Confirm.ask(f"Confirm read-only directory relationship collection for {dom} under engagement {eid}?", default=True):
                    with console.status("[bold green]Running BloodHound relationship collector...", spinner="dots"):
                        res = internal_path_mapping(dom, u, p)
                    render_internal_results(res, f"Path Mapping — {dom}")


def main():
    parser = argparse.ArgumentParser(description="M365 Deep Tenant OSINT, 20-Tool Recon & Self-Healing Canary Suite (TUI + Desktop GUI)")
    parser.add_argument("target", nargs="?", help="Optional positional domain or email to scan")
    parser.add_argument("-d", "--domain", help="Domain to check")
    parser.add_argument("-l", "--list", help="File containing a list of domains to check")
    parser.add_argument("-c", "--css", action="store_true", help="Always print raw Custom CSS when found")
    parser.add_argument("--gui", action="store_true", help="Launch full desktop GUI (PyWebView) instead of TUI")
    parser.add_argument("--no-tui", action="store_true", help="Exit after running CLI arguments")
    parser.add_argument("--recon", action="store_true", help="Run [10] Full Tenant Recon on target domain")
    parser.add_argument("--enum-users", help="Run [11] User Enumeration against a comma-separated list or file of emails")
    parser.add_argument("--enum-method", choices=["onedrive", "sso", "gct"], default="onedrive", help="User enumeration method for --enum-users")
    parser.add_argument("--probe-endpoints", action="store_true", help="Run [12] Endpoint & Azure DNS Discovery on target domain")
    parser.add_argument("--risk-matrix", action="store_true", help="Display [13] Detection Risk Matrix")
    parser.add_argument("--graph-token", help="Run [14] Authenticated Graph Enumeration with Bearer token")
    parser.add_argument("--knock", action="store_true", help="Run [K] Knock Mode (fire all canaries with Tool-Signature UA + SIEM burst)")
    parser.add_argument("--fire-vectors", help="Comma-separated vector keys (1-7 or 'all') to fire")
    parser.add_argument("--fire-url", help="Custom Canary URL to fire vectors against directly")
    parser.add_argument("--fire-referer", help="Referer override to send (empty string = omit)")
    parser.add_argument("--fire-ua", help="User-Agent override")

    args = parser.parse_args()
    domain_arg = args.domain or args.target

    # If launched with --gui or as a packaged PyInstaller --windowed executable without --no-tui
    if args.gui or (getattr(sys, "frozen", False) and not args.no_tui):
        launch_gui(domain_arg)
        return

    initial_results: List[ScanResult] = []

    if domain_arg:
        res = run_scan(domain_arg)
        initial_results.append(res)
        render_scan_result(res, show_css=args.css)
        if args.recon:
            render_tenant_recon(run_full_tenant_recon(res.domain))
        if args.probe_endpoints:
            render_endpoint_findings(probe_endpoints(res.domain, res.tenant_slug), res.domain)
        if args.enum_users:
            up = Path(args.enum_users)
            ulist = [ln.strip() for ln in up.read_text(encoding="utf-8").splitlines() if "@" in ln] if (up.exists() and up.is_file()) else [u.strip() for u in args.enum_users.split(",") if "@" in u]
            if args.enum_method == "onedrive" and res.tenant_slug:
                render_user_enum(enum_user_onedrive(res.tenant_slug, ulist), res.domain)
            elif args.enum_method == "sso":
                render_user_enum(enum_user_seamless_sso(res.domain, ulist), res.domain)
            else:
                render_user_enum(enum_user_getcredentialtype(ulist), res.domain)
        if args.knock:
            target_urls = [args.fire_url] if args.fire_url else res.canary_urls
            profile = ReconProfile(
                domain=res.domain,
                knock_mode=True,
                tool_signature_ua=args.fire_ua or "TeamFiltration/3.5.0",
                include_signin_knock=True,
                include_onedrive_knock=True,
                include_graph_knock=bool(args.graph_token),
                burst_count=25,
            )
            knock_res = run_knock_mode(
                profile=profile,
                urls=target_urls,
                preset_keys=list(TRIGGER_VECTOR_PRESETS.keys()),
                tenant_slug=res.tenant_slug,
                graph_token=args.graph_token,
            )
            render_knock_summary(knock_res)
        elif args.fire_vectors:
            target_urls = [args.fire_url] if args.fire_url else res.canary_urls
            if target_urls:
                overrides = {}
                if args.fire_referer is not None:
                    overrides["Referer"] = args.fire_referer
                if args.fire_ua:
                    overrides["User-Agent"] = args.fire_ua
                keys = (
                    list(TRIGGER_VECTOR_PRESETS.keys())
                    if args.fire_vectors.strip().lower() in ("a", "all")
                    else [k.strip() for k in args.fire_vectors.split(",") if k.strip() in TRIGGER_VECTOR_PRESETS]
                )
                attempts = fire_trigger_vectors(target_urls, keys, extra_header_overrides=overrides or None)
                render_trigger_attempts(attempts, res.domain)

    if args.risk_matrix:
        render_risk_matrix()
    if args.graph_token and not args.knock:
        render_graph_enumeration(run_graph_suite(args.graph_token))

    if args.no_tui and (domain_arg or args.list or args.fire_url or args.risk_matrix or args.graph_token or args.knock):
        sys.exit(0)

    interactive_tui(initial_results)


if __name__ == "__main__":
    main()

