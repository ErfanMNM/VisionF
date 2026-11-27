import { ApiError, readRequestJson } from './requests';
import type { Env } from './types';

const checkPath = '/api/updates/v1/check';
const downloadPath = '/api/updates/v1/download';
const responseHeaders = {
  'Cache-Control': 'no-store',
  'X-Content-Type-Options': 'nosniff',
};
const releaseUnavailable = 'Dịch vụ cập nhật tạm thời không khả dụng.';
const githubApiUrl = 'https://api.github.com/repos/ErfanMNM/VisionF/releases?per_page=100&page=1';
const githubDownloadOrigin = 'https://github.com';
const allowedDownloadOrigins = new Set([
  githubDownloadOrigin,
  'https://release-assets.githubusercontent.com',
  'https://objects.githubusercontent.com',
]);
const maxReleaseListBytes = 2 * 1024 * 1024;
const maxManifestBytes = 256 * 1024;
const maxPackageBytes = 256 * 1024 * 1024;

type ReleaseVersion = { text: string; parts: [number, number, number] };
type GithubAsset = { name?: unknown; size?: unknown; browser_download_url?: unknown };
type GithubRelease = {
  tag_name?: unknown;
  prerelease?: unknown;
  draft?: unknown;
  published_at?: unknown;
  assets?: unknown;
};
type SelectedRelease = { version: string; tag: string; assets: GithubAsset[] };

export async function handleUpdateRequest(request: Request, env: Env, path: string): Promise<Response> {
  if (path !== checkPath && path !== downloadPath) return error('Endpoint không tồn tại.', 404);
  if (request.method !== 'POST') return error('Chỉ hỗ trợ phương thức POST.', 405, { Allow: 'POST' });
  if (request.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json') {
    return error('Yêu cầu cần có Content-Type: application/json.', 415);
  }

  try {
    const { license, rid, channel, version } = await readRequestJson(request);
    if (rid !== 'win-x64' && rid !== 'linux-x64') throw new ApiError('Nền tảng cập nhật không hợp lệ.');
    if (channel !== 'stable' && channel !== 'pilot') throw new ApiError('Kênh cập nhật không hợp lệ.');
    if ((path === downloadPath || version !== undefined) && !isVersion(version)) {
      throw new ApiError('Phiên bản cập nhật không hợp lệ.');
    }

    // License remains authoritative and is rechecked before every metadata or asset request.
    const credential = readCredential(license);
    const stored = await env.DB.withSession('first-primary').prepare(`
      SELECT expires_at FROM licenses
      WHERE sig_b64 = ? AND data_b64 = ? AND product = 'TanTienQR' AND revoked_at IS NULL
      LIMIT 1
    `).bind(credential.sig, credential.data).first<{ expires_at: string }>();
    if (!stored || !(Date.parse(stored.expires_at) > Date.now())) {
      return error('License không có quyền tải bản cập nhật. Hãy liên hệ quản trị viên để kiểm tra hoặc cấp lại.', 403);
    }

    const release = await readChannel(channel, env);
    if (release === null) {
      return path === checkPath
        ? new Response(null, { status: 204, headers: responseHeaders })
        : error('Phiên bản trên kênh đã thay đổi. Hãy kiểm tra lại.', 409);
    }
    if (path === downloadPath && release.version !== version) {
      return error('Phiên bản trên kênh đã thay đổi. Hãy kiểm tra lại.', 409);
    }

    const assetName = path === checkPath ? `manifest-${rid}.json` : `package-${rid}.zip`;
    const asset = selectAsset(release, assetName, path === checkPath ? maxManifestBytes : maxPackageBytes);
    const response = await fetchAsset(asset, release.tag, assetName);
    const body = boundedBody(response.body, asset.size);
    if (path === checkPath) {
      return new Response(body, {
        headers: { ...responseHeaders, 'Content-Type': 'application/json; charset=utf-8' },
      });
    }
    return new Response(body, {
      headers: {
        ...responseHeaders,
        'Content-Type': 'application/zip',
        'Content-Length': String(asset.size),
        'Content-Disposition': `attachment; filename="TanTienQR-${release.version}-${rid}.zip"`,
      },
    });
  } catch (cause) {
    if (cause instanceof ApiError) return error(cause.message, cause.status);
    // Do not echo GitHub payloads, URLs, credentials, or request content to clients or logs.
    console.error('update_request_failed', path === checkPath ? 'check' : 'download');
    return error(releaseUnavailable, 503);
  }
}

async function readChannel(channel: 'stable' | 'pilot', env: Env): Promise<SelectedRelease | null> {
  const ttl = Number(env.UPDATE_METADATA_CACHE_TTL_SECONDS ?? '300');
  const init: RequestInit = {
    headers: {
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      'User-Agent': 'TanTienQR-Updater',
    },
  };
  if (Number.isSafeInteger(ttl) && ttl > 0) {
    // Short cache reduces GitHub API quota pressure. License checks stay uncached.
    init.cf = { cacheTtl: ttl, cacheEverything: true };
  }
  const response = await fetch(githubApiUrl, init);
  if (!response.ok) {
    await response.body?.cancel();
    throw new Error('GitHub release metadata is unavailable.');
  }
  const bytes = await readBounded(response, maxReleaseListBytes);
  const parsed: unknown = JSON.parse(new TextDecoder().decode(bytes));
  if (!Array.isArray(parsed)) throw new Error('GitHub release list is invalid.');

  const expectedPrerelease = channel === 'pilot';
  const candidates = parsed.flatMap((value): SelectedRelease[] => {
    if (!isRecord(value)) return [];
    const item = value as GithubRelease;
    const tag = item.tag_name;
    if (typeof tag !== 'string') return [];
    const releaseVersion = parseReleaseTag(tag);
    if (!releaseVersion || item.draft !== false || item.prerelease !== expectedPrerelease
      || typeof item.published_at !== 'string' || item.published_at.length === 0 || !Array.isArray(item.assets)) return [];
    return [{ version: releaseVersion.text, tag, assets: item.assets as GithubAsset[] }];
  });
  candidates.sort((a, b) => compareReleaseVersions(parseReleaseTag(b.tag)!, parseReleaseTag(a.tag)!));
  return candidates[0] ?? null;
}

function selectAsset(release: SelectedRelease, name: string, maxBytes: number): GithubAsset & { size: number; browser_download_url: string } {
  const matches = release.assets.filter((asset) => isRecord(asset) && asset.name === name);
  if (matches.length !== 1) throw new Error('Required release asset is missing or ambiguous.');
  const asset = matches[0];
  if (typeof asset.size !== 'number' || !Number.isSafeInteger(asset.size) || asset.size < 1 || asset.size > maxBytes
    || typeof asset.browser_download_url !== 'string') {
    throw new Error('Release asset metadata is invalid.');
  }

  // Only allow the exact browser_download_url for this repository, tag, and required asset.
  const expectedPath = `/${REPO_OWNER}/${REPO_NAME}/releases/download/${release.tag}/${name}`;
  const url = new URL(asset.browser_download_url);
  if (url.origin !== githubDownloadOrigin || url.pathname !== expectedPath || url.search || url.hash) {
    throw new Error('Release asset URL is invalid.');
  }
  return asset as GithubAsset & { size: number; browser_download_url: string };
}

const REPO_OWNER = 'ErfanMNM';
const REPO_NAME = 'VisionF';

async function fetchAsset(asset: GithubAsset & { size: number; browser_download_url: string }, tag: string, name: string): Promise<Response> {
  let url = asset.browser_download_url;
  let response: Response | null = null;
  for (let redirects = 0; redirects <= 3; redirects++) {
    response = await fetch(url, {
      headers: { Accept: 'application/octet-stream', 'User-Agent': 'TanTienQR-Updater' },
      redirect: 'manual',
    });
    if (response.status < 300 || response.status >= 400) break;
    const location = response.headers.get('Location');
    await response.body?.cancel();
    if (!location || redirects === 3) throw new Error('GitHub asset redirect is invalid.');
    const next = new URL(location, url);
    if (next.protocol !== 'https:' || !allowedDownloadOrigins.has(next.origin)) {
      throw new Error('GitHub asset redirected to an unsupported host.');
    }
    url = next.toString();
    response = null;
  }
  if (!response) throw new Error('GitHub asset redirect limit exceeded.');
  if (!response.ok) {
    await response.body?.cancel();
    throw new Error('GitHub release asset is unavailable.');
  }
  if (response.url) {
    const finalUrl = new URL(response.url);
    if (!allowedDownloadOrigins.has(finalUrl.origin)) {
      await response.body?.cancel();
      throw new Error('GitHub asset redirected to an unsupported host.');
    }
  }
  const contentLength = response.headers.get('Content-Length');
  if (contentLength !== null && (!/^\d+$/.test(contentLength) || Number(contentLength) !== asset.size)) {
    await response.body?.cancel();
    throw new Error('GitHub asset length does not match release metadata.');
  }
  // `tag` and `name` are intentionally consumed here to make the asset/release binding explicit.
  if (!asset.browser_download_url.endsWith(`/releases/download/${tag}/${name}`)) {
    await response.body?.cancel();
    throw new Error('GitHub asset does not match the selected release.');
  }
  return response;
}

async function readBounded(response: Response, maxBytes: number): Promise<Uint8Array> {
  const declaredLength = response.headers.get('Content-Length');
  if (declaredLength !== null && (!/^\d+$/.test(declaredLength) || Number(declaredLength) > maxBytes)) {
    await response.body?.cancel();
    throw new Error('GitHub response exceeds the allowed size.');
  }
  if (!response.body) throw new Error('GitHub response body is missing.');
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > maxBytes) {
        await reader.cancel();
        throw new Error('GitHub response exceeds the allowed size.');
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return bytes;
}

function boundedBody(body: ReadableStream<Uint8Array> | null, expectedBytes: number): ReadableStream<Uint8Array> {
  if (!body) throw new Error('GitHub asset body is missing.');
  let total = 0;
  return body.pipeThrough(new TransformStream<Uint8Array, Uint8Array>({
    transform(chunk, controller) {
      total += chunk.byteLength;
      if (total > expectedBytes) throw new Error('GitHub asset exceeds its declared size.');
      controller.enqueue(chunk);
    },
    flush() {
      if (total !== expectedBytes) throw new Error('GitHub asset does not match its declared size.');
    },
  }));
}

function readCredential(value: unknown): { data: string; sig: string } {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ApiError('Thiếu file license.');
  const license = value as Record<string, unknown>;
  if (license.v !== 1) throw new ApiError('Định dạng file license không hợp lệ.');
  return { data: normalizeBase64(license.data), sig: normalizeBase64(license.sig) };
}

function normalizeBase64(value: unknown): string {
  if (typeof value === 'string' && value.length > 0) {
    try {
      const bytes = atob(value);
      if (bytes.length > 0) return btoa(bytes);
    } catch { /* Báo lỗi định dạng chung, không phản hồi lại dữ liệu license. */ }
  }
  throw new ApiError('Định dạng file license không hợp lệ.');
}

function isVersion(value: unknown): value is string {
  return typeof value === 'string'
    && /^(?:0|[1-9][0-9]{0,9})(?:\.(?:0|[1-9][0-9]{0,9})){2}$/.test(value)
    && value.split('.').every(part => Number(part) <= 2_147_483_647);
}

function parseReleaseTag(tag: string): ReleaseVersion | null {
  if (!/^v(?:0|[1-9][0-9]{0,9})\.(?:0|[1-9][0-9]{0,9})\.(?:0|[1-9][0-9]{0,9})$/.test(tag)) return null;
  const text = tag.slice(1);
  if (!isVersion(text)) return null;
  return { text, parts: text.split('.').map(Number) as [number, number, number] };
}

function compareReleaseVersions(a: ReleaseVersion, b: ReleaseVersion): number {
  for (let i = 0; i < 3; i++) {
    if (a.parts[i] !== b.parts[i]) return a.parts[i] - b.parts[i];
  }
  return 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function error(message: string, status: number, headers?: Record<string, string>): Response {
  return Response.json({ error: message }, { status, headers: { ...responseHeaders, ...headers } });
}
