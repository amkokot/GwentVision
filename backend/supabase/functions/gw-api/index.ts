const API_VERSION = 1;
const MAX_BODY_BYTES = 9 * 1024 * 1024;
const MAX_MATCH_BYTES = 256 * 1024;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const PATCH = /^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?$/;
const FACTIONS = new Set(["Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate"]);
const RESULTS = new Set(["VICTORY", "DEFEAT", "DRAW"]);
const EVIDENCE = new Set(["Observed", "SelectedReference", "Inferred", "ManualHypothesis"]);
const ORIGINS = new Set(["Unknown", "ConfirmedStartingDeck", "ProbableStartingDeck", "Spawned", "Created",
  "Copied", "Transformed", "Replayed", "Summoned", "Stolen"]);
const CODE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

type JsonObject = Record<string, unknown>;
type RpcRow = Record<string, unknown>;

class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

function env(name: string): string {
  const value = Deno.env.get(name);
  if (!value) throw new ApiError(503, `Server configuration ${name} is unavailable.`);
  return value;
}

function allowedOrigin(request: Request): string | null {
  const origin = request.headers.get("origin");
  if (!origin) return null;
  const configured = (Deno.env.get("GV_ALLOWED_ORIGINS") ?? "https://amkokot.github.io,http://localhost:5173")
    .split(",").map((item) => item.trim()).filter(Boolean);
  if (!configured.includes(origin)) throw new ApiError(403, "Origin is not allowed.");
  return origin;
}

function response(request: Request, status: number, body: unknown, cache = "no-store"): Response {
  let origin: string | null = null;
  try { origin = allowedOrigin(request); } catch { /* Error responses deliberately omit CORS for rejected origins. */ }
  const headers: Record<string, string> = {
    "content-type": "application/json; charset=utf-8",
    "cache-control": cache,
    "x-content-type-options": "nosniff",
    "referrer-policy": "no-referrer",
  };
  if (origin) {
    headers["access-control-allow-origin"] = origin;
    headers["vary"] = "Origin";
  }
  return new Response(JSON.stringify(body), { status, headers });
}

function options(request: Request): Response {
  const origin = allowedOrigin(request);
  return new Response(null, { status: 204, headers: {
    "access-control-allow-origin": origin ?? "https://amkokot.github.io",
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "authorization, apikey, content-type",
    "access-control-max-age": "86400",
    "vary": "Origin",
  } });
}

function route(request: Request): string {
  const parts = new URL(request.url).pathname.split("/").filter(Boolean);
  const index = parts.lastIndexOf("gw-api");
  return "/" + (index < 0 ? parts : parts.slice(index + 1)).join("/");
}

async function bodyObject(request: Request, limit = MAX_BODY_BYTES): Promise<JsonObject> {
  const stated = Number(request.headers.get("content-length") ?? "0");
  if (stated > limit) throw new ApiError(413, "Request is too large.");
  const text = await request.text();
  if (new TextEncoder().encode(text).byteLength > limit) throw new ApiError(413, "Request is too large.");
  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error();
    return value;
  } catch { throw new ApiError(400, "Request must be a JSON object."); }
}

async function postgrest(path: string, init: RequestInit = {}): Promise<unknown> {
  const key = env("GV_SERVER_KEY");
  const result = await fetch(`${env("SUPABASE_URL")}/rest/v1/${path}`, {
    ...init,
    // Modern sb_secret_ keys are opaque API keys, not JWTs. Supabase translates
    // them to the service_role only when they are sent through `apikey`.
    headers: { "apikey": key, "content-type": "application/json",
      "accept": "application/json", ...(init.headers ?? {}) },
  });
  const text = await result.text();
  if (!result.ok) {
    let code = "unknown";
    try {
      const problem = JSON.parse(text) as JsonObject;
      if (typeof problem.code === "string" && /^[A-Z0-9]{5}$/.test(problem.code)) code = problem.code;
    } catch { /* Never echo the database body. */ }
    throw new ApiError(result.status >= 500 ? 503 : 400, `Database operation was rejected (${code}).`);
  }
  return text ? JSON.parse(text) : null;
}

async function rpc(name: string, parameters: JsonObject): Promise<RpcRow[]> {
  const value = await postgrest(`rpc/${name}`, { method: "POST", body: JSON.stringify(parameters) });
  return Array.isArray(value) ? value as RpcRow[] : value == null ? [] : [value as RpcRow];
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

function base64ToBytes(value: unknown, max: number): Uint8Array {
  if (typeof value !== "string" || value.length > Math.ceil(max * 4 / 3) + 8 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value))
    throw new ApiError(400, "Invalid base64 value.");
  try {
    const binary = atob(value);
    if (binary.length > max) throw new Error();
    return Uint8Array.from(binary, (character) => character.charCodeAt(0));
  } catch { throw new ApiError(400, "Invalid base64 value."); }
}

function base64Url(bytes: Uint8Array): string {
  return bytesToBase64(bytes).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function hex(bytes: ArrayBuffer | Uint8Array): string {
  const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  return Array.from(data, (value) => value.toString(16).padStart(2, "0")).join("");
}

function ownedBuffer(bytes: Uint8Array): ArrayBuffer {
  const copy = new Uint8Array(bytes.byteLength);
  copy.set(bytes);
  return copy.buffer;
}

async function sha256(bytes: Uint8Array): Promise<string> {
  return hex(await crypto.subtle.digest("SHA-256", ownedBuffer(bytes)));
}

function derToP1363(der: Uint8Array): Uint8Array {
  if (der.length < 8 || der[0] !== 0x30) throw new ApiError(401, "Signature is invalid.");
  let offset = 1;
  const sequenceLength = der[offset++] & 0x7f;
  if (sequenceLength !== der.length - offset || der[offset++] !== 0x02) throw new ApiError(401, "Signature is invalid.");
  const rLength = der[offset++];
  const r = der.subarray(offset, offset += rLength);
  if (der[offset++] !== 0x02) throw new ApiError(401, "Signature is invalid.");
  const sLength = der[offset++];
  const s = der.subarray(offset, offset += sLength);
  if (offset !== der.length || rLength > 33 || sLength > 33) throw new ApiError(401, "Signature is invalid.");
  const output = new Uint8Array(64);
  output.set(r.subarray(Math.max(0, r.length - 32)), 32 - Math.min(32, r.length));
  output.set(s.subarray(Math.max(0, s.length - 32)), 64 - Math.min(32, s.length));
  return output;
}

async function verifySignature(spki: Uint8Array, message: string, signatureDer: Uint8Array): Promise<void> {
  try {
    const key = await crypto.subtle.importKey("spki", ownedBuffer(spki),
      { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    const valid = await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key,
      ownedBuffer(derToP1363(signatureDer)), ownedBuffer(new TextEncoder().encode(message)));
    if (!valid) throw new Error();
  } catch { throw new ApiError(401, "Signature is invalid."); }
}

function requiredString(value: unknown, name: string, max: number, pattern?: RegExp): string {
  if (typeof value !== "string" || value.length < 1 || value.length > max || pattern && !pattern.test(value))
    throw new ApiError(400, `${name} is invalid.`);
  return value;
}

function optionalString(value: unknown, name: string, max: number): string | null {
  if (value == null) return null;
  return requiredString(value, name, max);
}

function integer(value: unknown, name: string, minimum: number, maximum: number, nullable = true): number | null {
  if (value == null && nullable) return null;
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum)
    throw new ApiError(400, `${name} is invalid.`);
  return value as number;
}

function boolean(value: unknown, name: string): boolean {
  if (typeof value !== "boolean") throw new ApiError(400, `${name} is invalid.`);
  return value;
}

function object(value: unknown, name: string): JsonObject {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new ApiError(400, `${name} is invalid.`);
  return value as JsonObject;
}

function exactKeys(value: JsonObject, name: string, allowed: readonly string[]): void {
  const allow = new Set(allowed);
  const unexpected = Object.keys(value).find((key) => !allow.has(key));
  if (unexpected) throw new ApiError(400, `${name} contains an unsupported field.`);
}

function array(value: unknown, name: string, maximum: number): unknown[] {
  if (!Array.isArray(value) || value.length > maximum) throw new ApiError(400, `${name} is invalid.`);
  return value;
}

function uuid(value: unknown, name: string): string { return requiredString(value, name, 36, UUID).toLowerCase(); }

function validateCard(value: unknown, expected: Set<string>): void {
  const card = object(value, "card");
  exactKeys(card, "card", ["cardId", "copies", "evidence", "origin", "confidence", "copyCountIsEstimate"]);
  requiredString(card.cardId, "cardId", 32, /^[A-Za-z0-9._:-]+$/);
  integer(card.copies, "copies", 1, 40, false);
  const evidence = requiredString(card.evidence, "evidence", 24);
  if (!EVIDENCE.has(evidence) || !expected.has(evidence)) throw new ApiError(400, "Card evidence collection is mixed.");
  if (!ORIGINS.has(requiredString(card.origin, "origin", 32))) throw new ApiError(400, "Card origin is invalid.");
  integer(card.confidence, "confidence", 0, 255, false);
  if (card.copyCountIsEstimate != null && boolean(card.copyCountIsEstimate, "copyCountIsEstimate") !==
      (evidence !== "SelectedReference")) throw new ApiError(400, "Card estimate marker is inconsistent.");
}

function validatePlayer(value: unknown): { faction: string | null } {
  const player = object(value, "player");
  exactKeys(player, "player", ["faction", "leader", "stratagem", "observations", "reference", "hypothesis"]);
  const faction = optionalString(player.faction, "faction", 32);
  if (faction && !FACTIONS.has(faction)) throw new ApiError(400, "Faction is invalid.");
  optionalString(player.leader, "leader", 120);
  optionalString(player.stratagem, "stratagem", 32);
  array(player.observations, "observations", 100).forEach((card) => validateCard(card, new Set(["Observed"])));
  array(player.reference, "reference", 100).forEach((card) => validateCard(card, new Set(["SelectedReference"])));
  array(player.hypothesis, "hypothesis", 100).forEach((card) => validateCard(card, new Set(["Inferred", "ManualHypothesis"])));
  return { faction };
}

async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  const compressed = new Blob([ownedBuffer(bytes)]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(compressed).arrayBuffer());
}

async function validateRecord(bytes: Uint8Array, requireInstallationIdentity = true): Promise<RpcRow> {
  let record: JsonObject;
  try { record = object(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)), "record"); }
  catch { throw new ApiError(400, "Match record is not valid UTF-8 JSON."); }
  exactKeys(record, "record", ["installationId", "matchId", "gameDateUtc", "detectorVersion", "rulesVersion",
    "patch", "patchInferred", "revision", "captureStopped", "resultObserved", "result", "mmrAfter",
    "mmrChange", "mmrPeak", "factionMmr", "rank", "user", "opponent", "rounds", "actions",
    "sequenceTruncated", "startedAtUtc", "mmrUnconfirmed"]);
  if (requireInstallationIdentity) uuid(record.installationId, "installationId");
  else if (record.installationId != null) uuid(record.installationId, "installationId");
  const matchId = uuid(record.matchId, "matchId");
  const gameDateUtc = requiredString(record.gameDateUtc, "gameDateUtc", 10, DATE);
  requiredString(record.detectorVersion, "detectorVersion", 80);
  requiredString(record.rulesVersion, "rulesVersion", 80);
  const patch = requiredString(record.patch, "patch", 32, PATCH);
  const patchInferred = boolean(record.patchInferred, "patchInferred");
  const revision = integer(record.revision, "revision", 1, Number.MAX_SAFE_INTEGER, false)!;
  const captureStopped = boolean(record.captureStopped, "captureStopped");
  const resultObserved = boolean(record.resultObserved, "resultObserved");
  const result = optionalString(record.result, "result", 12);
  if (result && !RESULTS.has(result)) throw new ApiError(400, "Result is invalid.");
  const mmrAfter = integer(record.mmrAfter, "mmrAfter", 0, 10000);
  const mmrChange = integer(record.mmrChange, "mmrChange", -1000, 1000);
  const mmrPeak = integer(record.mmrPeak, "mmrPeak", 0, 10000);
  const factionMmr = boolean(record.factionMmr, "factionMmr");
  const rank = integer(record.rank, "rank", 0, 30);
  const user = validatePlayer(record.user);
  const opponent = validatePlayer(record.opponent);
  array(record.rounds, "rounds", 3).forEach((value) => {
    const round = object(value, "round");
    exactKeys(round, "round", ["number", "userScore", "opponentScore", "finalConfirmed"]);
    integer(round.number, "round number", 1, 3, false);
    integer(round.userScore, "userScore", 0, 999); integer(round.opponentScore, "opponentScore", 0, 999);
    boolean(round.finalConfirmed, "finalConfirmed");
  });
  array(record.actions, "actions", 256).forEach((value) => {
    const action = object(value, "action");
    exactKeys(action, "action", ["cardId", "side", "round", "kind"]);
    optionalString(action.cardId, "action cardId", 32);
    const side = optionalString(action.side, "action side", 16);
    if (side && side !== "User" && side !== "Opponent") throw new ApiError(400, "Action side is invalid.");
    integer(action.round, "action round", 0, 3, false); requiredString(action.kind, "action kind", 40);
  });
  const sequenceTruncated = boolean(record.sequenceTruncated, "sequenceTruncated");
  const startedAt = optionalString(record.startedAtUtc, "startedAtUtc", 40);
  let playedAt = "";
  if (startedAt) {
    const time = new Date(startedAt);
    if (!Number.isFinite(time.valueOf()) || time.toISOString().slice(0, 10) !== gameDateUtc)
      throw new ApiError(400, "Match date and start time disagree.");
    playedAt = time.toISOString();
  }
  const mmrUnconfirmed = boolean(record.mmrUnconfirmed, "mmrUnconfirmed");
  const mmrConfirmed = factionMmr && !mmrUnconfirmed && mmrAfter != null && playedAt !== "";
  // The server-side source row replaces the local installation UUID. Play order
  // is intentionally not part of the central dataset, so it is validated and
  // then discarded before canonical compression.
  delete record.installationId;
  record.actions = [];
  const compact = await gzip(new TextEncoder().encode(JSON.stringify(record)));
  return {
    observationKey: matchId, clientMatchId: matchId, revision, playedAt, gameDateUtc, patch, patchInferred,
    result: resultObserved ? result ?? "" : "", playerFaction: user.faction ?? "",
    opponentFaction: opponent.faction ?? "", factionMmrAfter: factionMmr ? mmrAfter : null,
    factionMmrChange: factionMmr ? mmrChange : null, factionMmrPeak: factionMmr ? mmrPeak : null,
    standardRank: rank, mmrConfirmed, captureComplete: captureStopped, payloadFormat: "json-gzip-v1",
    payloadBase64: bytesToBase64(compact), qualityFlags: { patchInferred, mmrUnconfirmed, sequenceTruncated, resultObserved },
  };
}

async function keyedDigest(domain: string, value: string): Promise<string> {
  const pepper = base64ToBytes(env("GV_CODE_PEPPER"), 64);
  if (pepper.length < 32) throw new ApiError(503, "Server code secret is invalid.");
  const key = await crypto.subtle.importKey("raw", ownedBuffer(pepper),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", key,
    ownedBuffer(new TextEncoder().encode(`${domain}\n${value}`))));
}

const pepperDigest = (code: string) => keyedDigest("season-code", code);

async function consumeRateLimit(routeClass: string, digestDomain: string, identity: string): Promise<void> {
  const allowed = await postgrest("rpc/gw_consume_rate_limit", { method: "POST", body: JSON.stringify({
    p_route: routeClass, p_requester_digest_hex: await keyedDigest(digestDomain, identity),
  }) });
  if (allowed !== true) throw new ApiError(429, "Too many requests. Try again shortly.");
}

async function enforceNetworkRateLimit(request: Request, path: string): Promise<void> {
  const routeClass = path === "/register/challenge" || path === "/register" ? "register" :
    path === "/upload" ? "upload" : path === "/public/highlight" ? "highlight" :
    path === "/code/rotate" || path === "/curve/visibility" || path === "/data/delete" ? "device" :
    path.startsWith("/public/") ? "public" : path === "/analyst/export" ? "analyst" :
    path === "/collaborator/import" ? "collaborator" : null;
  if (!routeClass) return;
  const forwarded = request.headers.get("cf-connecting-ip") ?? request.headers.get("x-real-ip") ??
    request.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ?? "unavailable";
  await consumeRateLimit(routeClass, "network-address", forwarded);
}

function newCode(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(12));
  return Array.from(bytes, (value) => CODE_ALPHABET[value & 31]).join("");
}

function normalizedCode(value: unknown): string {
  const code = requiredString(value, "code", 32).toUpperCase().replaceAll(/[-\s]/g, "")
    .replaceAll("O", "0").replaceAll("I", "1").replaceAll("L", "1");
  if (code.length !== 12 || Array.from(code).some((character) => !CODE_ALPHABET.includes(character)))
    throw new ApiError(400, "Code is invalid.");
  return code;
}

async function installationKey(handle: string): Promise<Uint8Array> {
  const rows = await rpc("gw_get_installation_key", { p_installation_handle: handle });
  if (rows.length !== 1) throw new ApiError(401, "Installation is unavailable.");
  return base64ToBytes(rows[0].public_key_spki_base64, 512);
}

async function verifyDeviceEnvelope(operation: string, body: JsonObject): Promise<{ payload: JsonObject; digest: string }> {
  exactKeys(body, "signed request", ["payloadBase64", "signature", "timestamp"]);
  const payloadBytes = base64ToBytes(body.payloadBase64, MAX_BODY_BYTES);
  const signature = base64ToBytes(body.signature, 128);
  const timestamp = requiredString(body.timestamp, "timestamp", 40);
  const instant = new Date(timestamp);
  if (!Number.isFinite(instant.valueOf()) || Math.abs(Date.now() - instant.valueOf()) > 5 * 60 * 1000)
    throw new ApiError(401, "Signed request has expired.");
  let payload: JsonObject;
  try { payload = object(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(payloadBytes)), "payload"); }
  catch { throw new ApiError(400, "Signed payload is invalid."); }
  if (payload.schema !== API_VERSION) throw new ApiError(400, "API schema is unsupported.");
  const payloadKeys = operation === "upload"
    ? ["schema", "installationHandle", "batchId", "season", "producerVersion", "consentNoticeVersion",
      "publishAnonymousCurve", "matches"]
    : operation === "curve-visibility"
      ? ["schema", "installationHandle", "requestId", "season", "publishAnonymousCurve"]
      : ["schema", "installationHandle", "requestId", "season"];
  exactKeys(payload, "signed payload", payloadKeys);
  const handle = uuid(payload.installationHandle, "installationHandle");
  const requestId = uuid(operation === "upload" ? payload.batchId : payload.requestId, "requestId");
  const season = optionalString(payload.season, "season", 40) ?? "";
  const digest = await sha256(payloadBytes);
  const canonical = `GWENTVISION\n${API_VERSION}\n${operation}\n${handle}\n${requestId}\n${season}\n${digest}\n${instant.toISOString()}`;
  await verifySignature(await installationKey(handle), canonical, signature);
  await consumeRateLimit(operation === "upload" ? "upload" : "device", "installation-handle", handle);
  return { payload, digest };
}

async function registerChallenge(request: Request): Promise<Response> {
  const nonce = crypto.getRandomValues(new Uint8Array(32));
  const rows = await rpc("gw_create_registration_challenge", { p_nonce_digest_hex: await sha256(nonce) });
  if (rows.length !== 1) throw new ApiError(503, "Challenge could not be created.");
  return response(request, 200, { schema: API_VERSION, challengeId: rows[0].challenge_id,
    nonce: base64Url(nonce), expiresAt: rows[0].expires_at });
}

async function register(request: Request): Promise<Response> {
  const body = await bodyObject(request, 4096);
  exactKeys(body, "registration", ["challengeId", "nonce", "publicKeySpki", "signature"]);
  const challengeId = uuid(body.challengeId, "challengeId");
  const nonceText = requiredString(body.nonce, "nonce", 64, /^[A-Za-z0-9_-]+$/);
  const nonceBase64 = nonceText.replaceAll("-", "+").replaceAll("_", "/") +
    "=".repeat((4 - nonceText.length % 4) % 4);
  const nonce = base64ToBytes(nonceBase64, 64);
  const spki = base64ToBytes(body.publicKeySpki, 512);
  const fingerprint = await sha256(spki);
  const canonical = `GWENTVISION\n${API_VERSION}\nregister\n${challengeId}\n${nonceText}\n${fingerprint}`;
  await verifySignature(spki, canonical, base64ToBytes(body.signature, 128));
  const rows = await rpc("gw_complete_registration", { p_challenge_id: challengeId,
    p_nonce_digest_hex: await sha256(nonce), p_key_fingerprint_hex: fingerprint,
    p_public_key_spki_base64: bytesToBase64(spki) });
  if (rows.length !== 1) throw new ApiError(503, "Installation could not be registered.");
  return response(request, 200, { schema: API_VERSION, installationHandle: rows[0].installation_handle,
    registeredAt: rows[0].registered_at });
}

async function upload(request: Request): Promise<Response> {
  const body = await bodyObject(request);
  const { payload, digest } = await verifyDeviceEnvelope("upload", body);
  const matches = await Promise.all(array(payload.matches, "matches", 250).map(async (value) => {
    const envelope = object(value, "match");
    exactKeys(envelope, "match envelope", ["format", "recordBase64"]);
    if (envelope.format !== "json-v1") throw new ApiError(400, "Match format is unsupported.");
    return await validateRecord(base64ToBytes(envelope.recordBase64, MAX_MATCH_BYTES));
  }));
  if (matches.length === 0) throw new ApiError(400, "Upload contains no matches.");
  if (new Set(matches.map((match) => match.observationKey)).size !== matches.length)
    throw new ApiError(400, "Upload contains a duplicate match identity.");
  const patch = matches[0].patch;
  if (matches.some((match) => match.patch !== patch))
    throw new ApiError(400, "One upload batch may contain only one patch.");
  const season = requiredString(payload.season, "season", 40, /^[a-z0-9][a-z0-9._-]*$/);
  if (season !== patch) throw new ApiError(400, "Season must equal the match patch.");
  const code = newCode();
  const rows = await rpc("gw_accept_live_upload", {
    p_installation_handle: uuid(payload.installationHandle, "installationHandle"),
    p_season_slug: season,
    p_client_batch_id: uuid(payload.batchId, "batchId"), p_body_digest_hex: digest,
    p_producer_version: requiredString(payload.producerVersion, "producerVersion", 80),
    p_consent_notice_version: integer(payload.consentNoticeVersion, "consentNoticeVersion", 1, 1000, false),
    p_publish_anonymous_curve: boolean(payload.publishAnonymousCurve, "publishAnonymousCurve"),
    p_code_lookup_hex: await pepperDigest(code), p_matches: matches,
  });
  if (rows.length !== 1) throw new ApiError(503, "Upload did not produce a receipt.");
  return response(request, 200, { schema: API_VERSION, receiptId: rows[0].receipt_id,
    accepted: rows[0].accepted_count, unchanged: rows[0].unchanged_count,
    curveId: rows[0].curve_id, seasonCode: rows[0].code_created ? code : null });
}

async function highlight(request: Request): Promise<Response> {
  const body = await bodyObject(request, 2048);
  exactKeys(body, "highlight request", ["season", "code"]);
  const season = requiredString(body.season, "season", 40, /^[a-z0-9][a-z0-9._-]*$/);
  const rows = await rpc("gw_highlight_curve", { p_season_slug: season,
    p_code_lookup_hex: await pepperDigest(normalizedCode(body.code)) });
  return response(request, 200, { schema: API_VERSION, found: rows.length === 1,
    curveId: rows.length === 1 ? rows[0].curve_id : null });
}

async function rotateCode(request: Request): Promise<Response> {
  const body = await bodyObject(request, 8192);
  const { payload } = await verifyDeviceEnvelope("code-rotate", body);
  const code = newCode();
  const rows = await rpc("gw_rotate_season_code", {
    p_installation_handle: uuid(payload.installationHandle, "installationHandle"),
    p_season_slug: requiredString(payload.season, "season", 40, /^[a-z0-9][a-z0-9._-]*$/),
    p_code_lookup_hex: await pepperDigest(code),
  });
  if (rows.length !== 1) throw new ApiError(404, "Season contribution was not found.");
  return response(request, 200, { schema: API_VERSION, curveId: rows[0].curve_id, seasonCode: code });
}

async function curveVisibility(request: Request): Promise<Response> {
  const body = await bodyObject(request, 8192);
  const { payload } = await verifyDeviceEnvelope("curve-visibility", body);
  const rows = await rpc("gw_set_curve_visibility", {
    p_installation_handle: uuid(payload.installationHandle, "installationHandle"),
    p_season_slug: requiredString(payload.season, "season", 40, /^[a-z0-9][a-z0-9._-]*$/),
    p_publish: boolean(payload.publishAnonymousCurve, "publishAnonymousCurve"),
  });
  if (rows.length !== 1) throw new ApiError(404, "Season contribution was not found.");
  return response(request, 200, { schema: API_VERSION, curveId: rows[0].curve_id,
    publishAnonymousCurve: rows[0].published });
}

async function deleteData(request: Request): Promise<Response> {
  const body = await bodyObject(request, 8192);
  const { payload } = await verifyDeviceEnvelope("data-delete", body);
  const season = optionalString(payload.season, "season", 40);
  const rows = await rpc("gw_delete_installation_data", {
    p_installation_handle: uuid(payload.installationHandle, "installationHandle"), p_season_slug: season,
  });
  return response(request, 200, { schema: API_VERSION, deletedSeasons: rows[0]?.deleted_seasons ?? 0,
    deletedMatches: rows[0]?.deleted_matches ?? 0 });
}

async function publicSeasons(request: Request): Promise<Response> {
  const rows = await postgrest("published_seasons?select=slug,display_name,patch,starts_at,ends_at,active&order=starts_at.desc");
  return response(request, 200, { schema: API_VERSION, seasons: rows }, "public, max-age=300");
}

async function publicCurves(request: Request): Promise<Response> {
  const query = new URL(request.url).searchParams;
  const season = requiredString(query.get("season"), "season", 40, /^[a-z0-9][a-z0-9._-]*$/);
  const metric = requiredString(query.get("metric") ?? "total_mmr", "metric", 16);
  if (metric !== "total_mmr" && metric !== "faction_mmr") throw new ApiError(400, "Metric is invalid.");
  const seasonRows = await postgrest(`published_seasons?select=season_id&slug=eq.${encodeURIComponent(season)}&limit=1`) as RpcRow[];
  if (!Array.isArray(seasonRows) || seasonRows.length !== 1) throw new ApiError(404, "Season was not found.");
  const faction = query.get("faction") || null;
  if (metric === "faction_mmr" && faction && !FACTIONS.has(faction)) throw new ApiError(400, "Faction is invalid.");
  if (metric === "total_mmr" && faction) throw new ApiError(400, "Total MMR does not take a faction.");
  const offset = integer(Number(query.get("offset") ?? "0"), "offset", 0, 1_000_000, false)!;
  const pageSize = 5000;
  const filter = `published_curve_points?select=curve_id,bucket_at,metric,faction,value,point_order` +
    `&season_id=eq.${seasonRows[0].season_id}&metric=eq.${metric}` +
    (metric === "faction_mmr" && faction ? `&faction=eq.${encodeURIComponent(faction)}` : "") +
    `&order=curve_id.asc,faction.asc,point_order.asc&limit=${pageSize}&offset=${offset}`;
  const points = await postgrest(filter) as unknown[];
  return response(request, 200, { schema: API_VERSION, season, metric, faction, points,
    nextOffset: points.length === pageSize ? offset + pageSize : null },
    "public, max-age=300");
}

async function publicFactionDaily(request: Request): Promise<Response> {
  const query = new URL(request.url).searchParams;
  const season = requiredString(query.get("season"), "season", 40, /^[a-z0-9][a-z0-9._-]*$/);
  const rows = await rpc("gw_public_faction_daily", { p_season_slug: season });
  return response(request, 200, { schema: API_VERSION, season, minimumContributors: 5, rows },
    "public, max-age=300");
}

async function authenticatedUser(request: Request): Promise<string> {
  const authorization = request.headers.get("authorization");
  if (!authorization?.startsWith("Bearer ")) throw new ApiError(401, "Authentication is required.");
  const key = env("GV_SERVER_KEY");
  const result = await fetch(`${env("SUPABASE_URL")}/auth/v1/user`, { headers: { authorization, apikey: key } });
  if (!result.ok) throw new ApiError(401, "Authentication is invalid.");
  const user = await result.json();
  return uuid(user.id, "user id");
}

async function analystExport(request: Request): Promise<Response> {
  const userId = await authenticatedUser(request);
  const query = new URL(request.url).searchParams;
  const source = query.get("source") || null;
  if (source && !["live_game", "stream_archive", "collaborator_import"].includes(source))
    throw new ApiError(400, "Source is invalid.");
  const rows = await rpc("gw_analyst_export", { p_user_id: userId,
    p_season_slug: requiredString(query.get("season"), "season", 40, /^[a-z0-9][a-z0-9._-]*$/),
    p_after_id: integer(Number(query.get("after") ?? "0"), "after", 0, Number.MAX_SAFE_INTEGER, false),
    p_limit: integer(Number(query.get("limit") ?? "200"), "limit", 1, 500, false),
    p_source_kind: source });
  return response(request, 200, { schema: API_VERSION, rows });
}

async function collaboratorImport(request: Request): Promise<Response> {
  const userId = await authenticatedUser(request);
  await consumeRateLimit("collaborator", "auth-user", userId);
  const body = await bodyObject(request);
  exactKeys(body, "collaborator upload", ["schema", "batchId", "season", "datasetNamespace",
    "metadataSchemaVersion", "producerVersion", "sourceLocator", "matches"]);
  if (body.schema !== API_VERSION) throw new ApiError(400, "API schema is unsupported.");
  const batchId = uuid(body.batchId, "batchId");
  const season = requiredString(body.season, "season", 40, /^[a-z0-9][a-z0-9._-]*$/);
  const datasetNamespace = requiredString(body.datasetNamespace, "datasetNamespace", 80,
    /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/);
  const metadataSchemaVersion = integer(body.metadataSchemaVersion, "metadataSchemaVersion", 1, 1000, false)!;
  const producerVersion = requiredString(body.producerVersion, "producerVersion", 80,
    /^[A-Za-z0-9][A-Za-z0-9._+-]*$/);
  const sourceLocator = requiredString(body.sourceLocator, "sourceLocator", 160,
    /^[A-Za-z0-9][A-Za-z0-9._:/+-]*$/);
  const envelopes = array(body.matches, "matches", 250);
  if (envelopes.length === 0) throw new ApiError(400, "Upload contains no matches.");
  const matches = await Promise.all(envelopes.map(async (value) => {
    const envelope = object(value, "match");
    exactKeys(envelope, "match envelope", ["format", "recordBase64"]);
    if (envelope.format !== "json-v1") throw new ApiError(400, "Match format is unsupported.");
    return await validateRecord(base64ToBytes(envelope.recordBase64, MAX_MATCH_BYTES), false);
  }));
  if (new Set(matches.map((match) => match.observationKey)).size !== matches.length)
    throw new ApiError(400, "Upload contains a duplicate match identity.");
  const patch = matches[0].patch;
  if (matches.some((match) => match.patch !== patch) || season !== patch)
    throw new ApiError(400, "One upload batch must contain exactly one patch season.");
  const rows = await rpc("gw_accept_collaborator_match_upload", {
    p_user_id: userId, p_dataset_namespace: datasetNamespace,
    p_metadata_schema_version: metadataSchemaVersion,
    p_source_locator_digest_hex: await keyedDigest("collaborator-source", `${userId}\n${sourceLocator}`),
    p_season_slug: season, p_client_batch_id: batchId,
    p_body_digest_hex: await sha256(new TextEncoder().encode(JSON.stringify(body))),
    p_producer_version: producerVersion, p_matches: matches,
  });
  if (rows.length !== 1) throw new ApiError(503, "Upload did not produce a receipt.");
  return response(request, 200, { schema: API_VERSION, receiptId: rows[0].receipt_id,
    accepted: rows[0].accepted_count, unchanged: rows[0].unchanged_count });
}

Deno.serve(async (request: Request) => {
  try {
    allowedOrigin(request);
    if (request.method === "OPTIONS") return options(request);
    const path = route(request);
    await enforceNetworkRateLimit(request, path);
    if (request.method === "POST" && path === "/register/challenge") return await registerChallenge(request);
    if (request.method === "POST" && path === "/register") return await register(request);
    if (request.method === "POST" && path === "/upload") return await upload(request);
    if (request.method === "POST" && path === "/public/highlight") return await highlight(request);
    if (request.method === "POST" && path === "/code/rotate") return await rotateCode(request);
    if (request.method === "POST" && path === "/curve/visibility") return await curveVisibility(request);
    if (request.method === "POST" && path === "/data/delete") return await deleteData(request);
    if (request.method === "GET" && path === "/public/seasons") return await publicSeasons(request);
    if (request.method === "GET" && path === "/public/curves") return await publicCurves(request);
    if (request.method === "GET" && path === "/public/faction-daily") return await publicFactionDaily(request);
    if (request.method === "GET" && path === "/analyst/export") return await analystExport(request);
    if (request.method === "POST" && path === "/collaborator/import") return await collaboratorImport(request);
    return response(request, 404, { error: "Route not found." });
  } catch (error) {
    const status = error instanceof ApiError ? error.status : 500;
    const message = error instanceof ApiError ? error.message : "Unexpected server error.";
    return response(request, status, { error: message });
  }
});
