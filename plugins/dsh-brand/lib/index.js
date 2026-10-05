/**
 * dsh-brand host half: serves the brand configuration over a local HTTP API.
 *
 * The config lives at `$DSH_HOME/dsh-brand.json` (default `~/.dsh/dsh-brand.json`)
 * so one file brands every workspace and can be shipped as-is by downstream
 * forks. All fields are optional strings; an empty field keeps the stock GUI
 * fallback for that slot.
 *
 * Routes (registered on BOTH paths; the client tries them in order):
 *  - GET  /dsh-brand/config  and  /api/dsh-brand/config   -> the sanitized config
 *  - PUT  /dsh-brand/config  and  /api/dsh-brand/config   <- a full or partial
 *                                    config object; writes the file and returns
 *                                    the sanitized result
 */
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { dirname, extname, isAbsolute, join, parse, relative, resolve } from 'node:path'

export const name = 'dsh-brand'
export const inject = ['webServer']

const FILE_NAME = 'dsh-brand.json'
const LIMITS = {
  name: 100000,
  version: 100000,
  useDshVersion: 8,
  headline: 100000,
  badge: 100000,
  intro: 100000,
  logoText: 8,
  logoUrl: 200000,
  logoUrlDark: 200000,
  title: 60,
  favicon: 200000,
  faviconDark: 200000,
  sendIcon: 200000,
  stopIcon: 200000,
  hideNotice: 8,
  hideName: 8,
  thinkText: 60,
  colorEnabled: 8,
  colorTargets: 160,
  color: 32,
  colorDark: 32,
  hideHeadline: 8,
  markHeight: 8,
  heroMarkHeight: 8,
  // 桌面启动屏（DshDesktop）专用：桌面壳直接读同一份 $DSH_HOME/dsh-brand.json，
  // 这两个字段不影响 Web 界面，改完需要重启 DshDesktop 生效。
  bootSlogan: 400,
  desktopLogoHeight: 8,
}
const KEYS = Object.keys(LIMITS)

/** @returns every field empty (stock GUI branding). */
function defaults() {
  return { name: '', version: '', useDshVersion: 'true', headline: '', badge: '', intro: '', logoText: '', logoUrl: '', logoUrlDark: '', title: '', favicon: '', faviconDark: '', sendIcon: '', stopIcon: '', hideNotice: '', hideName: '', hideHeadline: '', thinkText: '', colorEnabled: 'true', colorTargets: 'sidebar,project,think,diving,composer', color: '', colorDark: '', markHeight: '', heroMarkHeight: '', bootSlogan: '', desktopLogoHeight: '' }
}

/** Resolve the running DSH build badge without persisting it in brand config. */
function dshBuildVersion() {
  const explicit = typeof process.env.DSH_CLIENT_COMMIT_HASH === 'string' ? process.env.DSH_CLIENT_COMMIT_HASH.trim() : ''
  if (/^[0-9a-f]{7,40}$/i.test(explicit)) return explicit.slice(0, 7)
  let current = dirname(process.argv[1] || process.cwd())
  const root = parse(current).root
  while (current !== root) {
    if (existsSync(join(current, 'package.json'))) {
      try {
        const pkg = JSON.parse(readFileSync(join(current, 'package.json'), 'utf8'))
        if (pkg && (pkg.name === '@deepseek-ai/dsh' || pkg.name === 'deepseek-harness')) {
          try {
            return execFileSync('git', ['-C', current, 'rev-parse', '--short=7', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim()
          } catch {
            if (typeof pkg.version === 'string' && pkg.version !== '') return pkg.version
          }
        }
      } catch { /* continue walking */ }
    }
    current = dirname(current)
  }
  return ''
}

const DSH_BUILD_VERSION = dshBuildVersion()
function publicConfig() {
  const cfg = readConfig()
  const out = { ...cfg }
  // 渲染值：把相对路径解析成 Data URL，写成 *Resolved，设置界面依旧显示用户填的原始值。
  for (const f of LOCAL_IMAGE_FIELDS) out[f + 'Resolved'] = resolveLocalImage(cfg[f])
  out.dshBuildVersion = DSH_BUILD_VERSION
  return out
}

/**
 * Keep only known string fields, trimmed to their per-field caps.
 * @param input - untrusted parsed JSON.
 * @returns a complete config object.
 */
function sanitize(input) {
  const out = defaults()
  if (input === null || typeof input !== 'object') return out
  for (const key of KEYS) {
    const value = input[key]
    if (typeof value === 'string') out[key] = value.slice(0, LIMITS[key])
  }
  // Auto-convert raw SVG markup to data URLs for image-capable fields.
  const imageFields = ['logoUrl', 'logoUrlDark', 'favicon', 'faviconDark', 'sendIcon', 'stopIcon', 'name', 'version', 'headline', 'badge', 'intro']
  for (const f of imageFields) {
    // 只把原始 SVG 代码转成 Data URL；本地相对路径保持原样返回（供设置界面显示/编辑），
    // 渲染用的解析值由 publicConfig() 额外给一份 *Resolved 字段。
    if (/<svg/i.test(out[f])) out[f] = ('data:image/svg+xml,' + encodeURIComponent(out[f])).slice(0, LIMITS[f])
  }
  return out
}

/** 需要解析成本地 Data URL 的图片字段（相对 $DSH_HOME 的本地路径）。 */
const LOCAL_IMAGE_FIELDS = ['logoUrl', 'logoUrlDark', 'favicon', 'faviconDark', 'sendIcon', 'stopIcon', 'name', 'version', 'headline', 'badge', 'intro']

/**
 * 简化 logo 导入：图片字段里写**本地相对路径**时，Host 直接读文件并转成 Data URL。
 * 相对基准是 $DSH_HOME（配置文件所在目录），例如 `./brand/logo.svg`；
 * 只允许读取 $DSH_HOME 目录内的文件，超过 140 KB 的图片按未配置处理
 * （字段上限 20 万字符，base64 会膨胀约 1.33 倍）。
 */
function resolveLocalImage(value) {
  if (typeof value !== 'string' || value === '') return value
  if (/^(https?:|data:|blob:)/i.test(value) || /<svg/i.test(value)) return value
  if (!/\.(svg|png|jpe?g|webp|gif|ico)$/i.test(value)) return value
  try {
    const base = dirname(configPath())
    const abs = resolve(base, value)
    const rel = relative(base, abs)
    if (rel.startsWith('..') || isAbsolute(rel)) return value
    if (!existsSync(abs) || !statSync(abs).isFile()) return value
    if (statSync(abs).size > 140 * 1024) return value
    const ext = extname(abs).toLowerCase()
    const mime = {
      '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg',
      '.webp': 'image/webp', '.gif': 'image/gif', '.ico': 'image/x-icon',
    }[ext] || 'application/octet-stream'
    const buffer = readFileSync(abs)
    return mime === 'image/svg+xml'
      ? 'data:image/svg+xml,' + encodeURIComponent(buffer.toString('utf8'))
      : 'data:' + mime + ';base64,' + buffer.toString('base64')
  } catch (_error) {
    return value
  }
}

/** Absolute path of the config file: $DSH_HOME/dsh-brand.json, defaulting to ~/.dsh. */
function configPath() {
  const env = process.env.DSH_HOME
  const home = typeof env === 'string' && env.trim() !== '' ? env : join(homedir(), '.dsh')
  return join(home, FILE_NAME)
}

/** Read the config file; any failure (missing, malformed) yields the defaults. */
function readConfig() {
  try {
    const path = configPath()
    if (!existsSync(path)) return defaults()
    return sanitize(JSON.parse(readFileSync(path, 'utf8')))
  } catch {
    return defaults()
  }
}

/** Persist the config, creating $DSH_HOME when needed. */
function writeConfig(cfg) {
  const path = configPath()
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, JSON.stringify(cfg, null, 2) + '\n', 'utf8')
}

function sendJson(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
  res.end(JSON.stringify(body))
}

/** Collect a JSON request body with a hard 512 KB ceiling. */
function readBody(req) {
  return new Promise((resolve, reject) => {
    let text = ''
    req.on('data', (chunk) => {
      text += chunk
      if (text.length > 512 * 1024) {
        reject(new Error('request body too large'))
        req.destroy()
      }
    })
    req.on('end', () => {
      try {
        resolve(text === '' ? {} : JSON.parse(text))
      } catch (error) {
        reject(error)
      }
    })
    req.on('error', reject)
  })
}

/**
 * Mutations require a same-origin browser call: requests with no Origin header
 * (same-origin navigations, curl) pass; a cross-site Origin must not.
 */
function sameOrigin(req) {
  const origin = req.headers?.origin
  if (typeof origin !== 'string' || origin === '') return true
  try {
    return new URL(origin).host === req.headers.host
  } catch {
    return false
  }
}

/**
 * 配置端点同时挂两条路径：核心不同版本 / 不同组合可能把插件路由挂在根路径，
 * 也可能挂在 `/api` 前缀下，实测两种情况都出现过。客户端按候选顺序回退，
 * 宿主这里两条都注册，任一存在即可读写。
 */
const CONFIG_ROUTES = ['/dsh-brand/config', '/api/dsh-brand/config']

/** 记录一条注册失败，但绝不让另一条注册跟着失败。 */
function logRouteFailure(ctx, path, error) {
  const detail = error instanceof Error ? error.message : String(error)
  try {
    const line = `[dsh-brand] route ${path} registration failed: ${detail}`
    if (ctx && ctx.logger && typeof ctx.logger.warn === 'function') ctx.logger.warn(line)
    else console.error(line)
  } catch (_ignored) {
    /* logging must never break registration */
  }
}

export function apply(ctx) {
  // 两条路径共用同一个 handler：同源校验、读体、写文件、回读逻辑只有一份。
  const handler = async (req, res) => {
    try {
      if (req.method === 'GET') {
        sendJson(res, 200, publicConfig())
        return
      }
      if (req.method === 'PUT' || req.method === 'POST') {
        if (!sameOrigin(req)) {
          sendJson(res, 403, { error: 'untrusted origin' })
          return
        }
        const next = sanitize(await readBody(req))
        writeConfig(next)
        // 返回和 GET 完全同构的结果（含 *Resolved 渲染值），
        // 否则界面保存后拿不到解析后的图片值，会把相对路径当文字渲染。
        sendJson(res, 200, publicConfig())
        return
      }
      res.writeHead(405, { allow: 'GET, PUT, POST' })
      res.end()
    } catch (error) {
      sendJson(res, 500, { error: error instanceof Error ? error.message : String(error) })
    }
  }

  ctx.effect(() => {
    const disposers = []
    for (const path of CONFIG_ROUTES) {
      try {
        disposers.push(ctx.webServer.register({
          kind: 'exact',
          path,
          methods: ['GET', 'PUT', 'POST'],
          handler,
        }))
      } catch (error) {
        // 例如该路径已被占用（重复注册）。吞掉这一条的错误，继续注册下一条。
        logRouteFailure(ctx, path, error)
      }
    }
    return () => {
      for (const dispose of disposers) {
        try { dispose() } catch (error) { /* double dispose is harmless */ }
      }
    }
  }, 'dsh-brand: config route')

  // Stamp the current config into the served HTML so the client bundle applies
  // branding synchronously at boot — no fallback-logo flash while a fetch is
  // in flight. `<` is escaped so config text can never break out of the tag.
  ctx.effect(() => ctx.webServer.tapIndex((html) => {
    const cfg = publicConfig()
    const json = JSON.stringify(cfg).replace(/</g, '\\u003c')
    const tag = `<script>window.__DSH_BRAND__=${json};</` + 'script>'
    let out = html
    // Replace the static <title> tag with the configured product title.
    if (cfg.title !== '') {
      const titleText = cfg.title.replace(/</g, '&lt;')
      out = out.replace(/<title>[^<]*<\/title>/, '<title>' + titleText + '</title>')
    }
    // Replace the favicon link href with the configured icon (if any).
    const favicon = cfg.favicon !== '' ? cfg.favicon : cfg.logoUrl
    if (favicon !== '') {
      const fav = favicon.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;')
      out = out.replace(/(<link\s+rel="icon"[^>]*href=")[^"]*"/, '$1' + fav + '"')
    }
    const at = out.indexOf('</head>')
    return at === -1 ? out + tag : out.slice(0, at) + tag + out.slice(at)
  }), 'dsh-brand: index config injection')
}
