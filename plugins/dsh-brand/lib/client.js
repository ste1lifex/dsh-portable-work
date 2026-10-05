/**
 * dsh-brand client bundle (official dsh client-package shape):
 * window.__ModuleLoader__.load({ id, factory }) with `require` resolving the
 * shell's shared externals ("react" here; see apps web platform seed table).
 *
 * Replaces the GUI branding through the stock brand slots:
 *  - sidebar.brand.mark / sidebar.brand.name  (logo, name, version badge)
 *  - conversation.hero.brand.mark             (hero logo; when any hero text
 *    field is set, the occupant renders the whole headline row and a scoped
 *    stylesheet hides the shell's stock headline/badge spans)
 *  - document.title                           (tab title, re-asserted because
 *    the shell rewrites it on every session change)
 * Config arrives from GET /api/dsh-brand/config; the settings page writes it
 * back with PUT. Every registration is disposed on unload.
 */
window.__ModuleLoader__.load({
  id: 'dsh-brand',
  factory: (require) => {
    const React = require('react')
    const h = React.createElement
    const API = '/api/dsh-brand/config'
    const KEYS = ['name', 'version', 'useDshVersion', 'headline', 'badge', 'intro', 'logoText', 'logoUrl', 'logoUrlDark', 'title', 'favicon', 'faviconDark', 'sendIcon', 'stopIcon', 'hideNotice', 'hideName', 'hideHeadline', 'thinkText', 'colorEnabled', 'colorTargets', 'color', 'colorDark', 'markHeight', 'heroMarkHeight', 'bootSlogan', 'desktopLogoHeight']
    const RUNTIME_KEYS = KEYS.concat(['dshBuildVersion']).concat(KEYS.map((k) => k + 'Resolved'))
    const ALL_COLOR_TARGETS = 'sidebar,project,think,diving,composer'

    /** Every field empty: stock GUI branding; color feature remains enabled. */
    function blank() {
      return { name: '', version: '', useDshVersion: 'true', dshBuildVersion: '', headline: '', badge: '', intro: '', logoText: '', logoUrl: '', logoUrlDark: '', title: '', favicon: '', faviconDark: '', sendIcon: '', stopIcon: '', hideNotice: '', hideName: '', hideHeadline: '', thinkText: '', colorEnabled: 'true', colorTargets: ALL_COLOR_TARGETS, color: '', colorDark: '', markHeight: '', heroMarkHeight: '', bootSlogan: '', desktopLogoHeight: '' }
    }

    /**
     * 首屏注入的 window.__DSH_BRAND__（服务端每次请求都按当前配置重算）。
     * 设置界面的 fetch 失败时（例如 cookie 不一致导致 401）用它兜底，
     * 否则表单会被 blank() 填成全空 —— 看起来就像"什么都没配"。
     */
    function bootConfig() {
      const boot = typeof window === 'object' && window !== null ? window.__DSH_BRAND__ : undefined
      return boot !== null && typeof boot === 'object' ? boot : null
    }

    /** 空字段用首屏注入的配置补齐。 */
    function mergeBoot(cfg) {
      const boot = bootConfig()
      if (boot === null) return cfg
      const out = { ...cfg }
      for (const key of KEYS) {
        const has = typeof out[key] === 'string' && out[key] !== ''
        if (!has && typeof boot[key] === 'string' && boot[key] !== '') out[key] = boot[key]
      }
      return out
    }

    /** Keep only known string fields (server already caps their length). */
    function clean(input) {
      const out = blank()
      if (input === null || typeof input !== 'object') return out
      for (const key of RUNTIME_KEYS) {
        const value = input[key]
        if (typeof value === 'string') out[key] = value
      }
      return out
    }

    /**
     * If the value is raw SVG markup (starts with <svg), wrap it in a
     * data URL so it works as an <img src> / <link href>. Already-valid
     * URLs (http:, data:, /) pass through unchanged.
     */
    function ensureDataUrl(value) {
      if (typeof value !== 'string' || value === '') return ''
      if (/^(https?:|data:|\/|blob:)/i.test(value)) return value
      if (/<svg/i.test(value)) return 'data:image/svg+xml,' + encodeURIComponent(value)
      return value
    }

    /** True when a field value should render as an <img> rather than text. */
    function isImageValue(v) {
      if (typeof v !== 'string' || v === '') return false
      if (/^data:image\//i.test(v) || /^https?:\/\//i.test(v) || v[0] === '/' || /<svg/i.test(v)) return true
      // 本地相对路径（./brand/x.svg、../brand/x.png）也要当图片，否则会被当文字渲染。
      return /^\.{1,2}[\\/][^\s]*\.(svg|png|jpe?g|webp|gif|ico)$/i.test(v)
    }

    /** Best synchronous SVG fallback while the pixel sampler is loading. */
    function svgFallbackColor(value) {
      if (typeof value !== 'string' || value === '') return ''
      let text = value
      try {
        if (/^data:image\/svg\+xml,/i.test(value)) text = decodeURIComponent(value.slice(value.indexOf(',') + 1))
        else if (/^data:image\/svg\+xml;base64,/i.test(value)) text = window.atob(value.slice(value.indexOf(',') + 1))
      } catch (error) { return '' }
      if (!/<svg/i.test(text)) return ''
      const colors = []
      const re = /(?:stop-color|fill|stroke)\s*=\s*["'](#[0-9a-fA-F]{6})["']/g
      let match
      while ((match = re.exec(text)) !== null) {
        const color = match[1].toLowerCase()
        if (color !== '#ffffff' && color !== '#000000') colors.push(color)
      }
      return colors.length === 0 ? '' : colors[Math.floor(colors.length / 2)]
    }

    /**
     * Render the logo to a tiny canvas and return its dominant visible color.
     * RGB values are quantized into buckets so gradients resolve to their
     * largest color region rather than hundreds of one-pixel colors.
     */
    function sampleDominantColor(value) {
      const fallback = svgFallbackColor(value)
      if (!isImageValue(value)) return Promise.resolve(fallback)
      return new Promise((resolve) => {
        const image = new window.Image()
        let settled = false
        const finish = (color) => {
          if (settled) return
          settled = true
          resolve(color || fallback)
        }
        image.onload = () => {
          try {
            const canvas = document.createElement('canvas')
            canvas.width = 48
            canvas.height = 48
            const context = canvas.getContext('2d', { willReadFrequently: true })
            if (context === null) { finish(fallback); return }
            const scale = Math.min(48 / image.naturalWidth, 48 / image.naturalHeight)
            const width = Math.max(1, image.naturalWidth * scale)
            const height = Math.max(1, image.naturalHeight * scale)
            context.clearRect(0, 0, 48, 48)
            context.drawImage(image, (48 - width) / 2, (48 - height) / 2, width, height)
            const pixels = context.getImageData(0, 0, 48, 48).data
            const buckets = new Map()
            for (let i = 0; i < pixels.length; i += 4) {
              const r = pixels[i]
              const g = pixels[i + 1]
              const b = pixels[i + 2]
              const a = pixels[i + 3]
              if (a < 96 || (r > 245 && g > 245 && b > 245)) continue
              const key = (Math.floor(r / 24) << 8) | (Math.floor(g / 24) << 4) | Math.floor(b / 24)
              const bucket = buckets.get(key) || { count: 0, r: 0, g: 0, b: 0 }
              bucket.count += 1
              bucket.r += r
              bucket.g += g
              bucket.b += b
              buckets.set(key, bucket)
            }
            let best = null
            for (const bucket of buckets.values()) {
              if (best === null || bucket.count > best.count) best = bucket
            }
            if (best === null) { finish(fallback); return }
            const hex = (n) => Math.round(n / best.count).toString(16).padStart(2, '0')
            finish('#' + hex(best.r) + hex(best.g) + hex(best.b))
          } catch (error) { finish(fallback) }
        }
        image.onerror = () => finish(fallback)
        if (/^https?:\/\//i.test(value)) image.crossOrigin = 'anonymous'
        image.src = ensureDataUrl(value)
        window.setTimeout(() => finish(fallback), 3000)
      })
    }

    /**
     * 深色主题判定：优先看 DSH 在 html/body 上挂的 class 标记，其次看解析后的
     * color-scheme，最后退回系统偏好。favicon 没法用 CSS 切，所以需要真正判定。
     */
    function isDarkTheme() {
      try {
        const root = document.documentElement
        const body = document.body
        const has = (el, cls) => !!el && !!el.classList && el.classList.contains(cls)
        if (has(root, 'dark') || has(body, 'dark')) return true
        if (has(root, 'light') || has(body, 'light')) return false
        const cs = (window.getComputedStyle(root).colorScheme || '') + ' ' + ((root.style && root.style.colorScheme) || '')
        if (/dark/i.test(cs) && !/light/i.test(cs)) return true
        // 最可靠的一路：看界面实际的背景色亮度（不依赖 DSH 用什么 class/属性标记主题）。
        const probe = (el) => {
          if (!el) return null
          const bg = window.getComputedStyle(el).backgroundColor || ''
          const m = /rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)/.exec(bg)
          if (!m) return null
          const alpha = /rgba\(\s*\d+[,\s]+\d+[,\s]+\d+[,\s]+([\d.]+)/.exec(bg)
          if (alpha && Number(alpha[1]) < 0.5) return null
          return (0.299 * Number(m[1]) + 0.587 * Number(m[2]) + 0.114 * Number(m[3])) / 255
        }
        const luminance = probe(body) ?? probe(root)
        if (luminance !== null) return luminance < 0.5
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches)
      } catch (_error) {
        return false
      }
    }

    /**
     * 强调色支持「暗色专用值」：把配色规则里的颜色字面量抽成 CSS 变量
     * --dsh-brand-accent，再在暗色作用域下覆盖为 colorDark（默认等于 color）。
     * 这样侧栏/项目/思考/运行态/发送按钮等所有目标一次生效，无需逐个选择器再写一遍。
     */
    function withAccentTheme(css, cfg) {
      if (cfg.colorEnabled === 'false') return css
      const base = String(cfg.color || '')
      if (!/^#[0-9a-fA-F]{6,8}$/.test(base)) return css
      const dark = cfg.colorDark !== '' ? cfg.colorDark : base
      const accent = 'var(--dsh-brand-accent)'
      return ':root{--dsh-brand-accent:' + base + ';}'
        + 'html.dark,body.dark,html[style*="color-scheme: dark"]{--dsh-brand-accent:' + dark + ';}'
        + css.split(base).join(accent)
    }

    /**
     * 按「DSH 当前主题」在 <html> 上打一个 class，供 CSS 切换双 logo。
     * 不用 @media (prefers-color-scheme)：系统深色但 DSH 选了浅色时会选错版本
     * （把白色 logo 画在白底上 = 什么都看不见）。
     */
    function syncThemeFlag() {
      try {
        document.documentElement.classList.toggle('dsh-brand-dark', isDarkTheme())
      } catch (_error) { /* ignore */ }
    }

    /** 监听主题变化（class/style 改动 + 系统偏好变化）。 */
    function watchTheme() {
      syncThemeFlag()
      try {
        const observer = new MutationObserver(() => syncThemeFlag())
        observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'style'] })
        if (document.body) observer.observe(document.body, { attributes: true, attributeFilter: ['class', 'style'] })
        if (window.matchMedia) {
          const media = window.matchMedia('(prefers-color-scheme: dark)')
          if (media.addEventListener) media.addEventListener('change', () => syncThemeFlag())
        }
      } catch (_error) { /* ignore */ }
    }
    watchTheme()

    /** 标志高度：markHeight（侧栏）/ heroMarkHeight（Hero）可覆盖 slot 给的尺寸。 */
    function markSize(cfg, size, role) {
      const raw = role === 'hero' ? cfg.heroMarkHeight : cfg.markHeight
      const n = Math.round(Number(raw))
      if (Number.isFinite(n) && n > 0) return n
      // 默认尺寸：侧栏 30px / Hero 60px（字段留空即用这两个值，而不是 slot 给的小尺寸）。
      return role === 'hero' ? 60 : 30
    }

    /**
     * The logo mark: image (URL / data URL / SVG) or text/emoji, auto-detected.
     * 配了 logoUrlDark 时两张图都渲染，由 CSS 按当前主题只显示其一 ——
     * 切深/浅色不需要重渲染，也不会丢 slot 内容。
     */
    function markElement(cfg, size, role) {
      const px = markSize(cfg, size, role)
      const light = (cfg.logoUrlResolved || cfg.logoUrl) !== '' ? (cfg.logoUrlResolved || cfg.logoUrl) : cfg.logoText
      const darkRaw = cfg.logoUrlDarkResolved || cfg.logoUrlDark
      const dark = darkRaw !== ''
        ? (isImageValue(darkRaw) ? darkRaw : '')
        : ''
      if (dark !== '' && light !== '' && isImageValue(light)) {
        const alt = cfg.name !== '' ? cfg.name : 'brand'
        return h('span', { className: 'dsh-brand-markswap' },
          h('img', { className: 'dsh-brand-markimg dsh-brand-mark-light', src: ensureDataUrl(light), alt, style: { height: px + 'px', width: 'auto' } }),
          h('img', { className: 'dsh-brand-markimg dsh-brand-mark-dark', src: ensureDataUrl(dark), alt, style: { height: px + 'px', width: 'auto' } }))
      }
      const logo = light
      if (logo !== '') {
        if (isImageValue(logo)) {
          return h('img', {
            className: 'dsh-brand-markimg',
            src: ensureDataUrl(logo),
            alt: cfg.name !== '' ? cfg.name : 'brand',
            style: { height: px + 'px', width: 'auto' },
          })
        }
        return h('span', {
          className: 'dsh-brand-letter',
          style: { width: px + 'px', height: px + 'px', fontSize: Math.max(10, Math.round(px * 0.52)) + 'px' },
        }, logo)
      }
      if (cfg.name !== '' && !isImageValue(cfg.name)) {
        return h('span', {
          className: 'dsh-brand-letter',
          style: { width: px + 'px', height: px + 'px', fontSize: Math.max(10, Math.round(px * 0.52)) + 'px' },
        }, Array.from(cfg.name)[0])
      }
      return null
    }

    /** Sidebar name row: product name plus the version badge (single host element — slot-safe). */
    function nameElement(cfg) {
      // hideName='true'：logo 自带文字时不再重复显示产品名（版本徽标保留）。
      const hideName = cfg.hideName === 'true'
      const version = cfg.version !== '' ? cfg.version : (cfg.name !== '' && cfg.useDshVersion !== 'false' ? cfg.dshBuildVersion : '')
      return h('span', { className: 'dsh-brand-namewrap' },
        (!hideName && cfg.name !== '') ? (isImageValue(cfg.name)
          ? h('img', { className: 'dsh-brand-nameimg', src: ensureDataUrl(cfg.name), alt: 'brand name' })
          : h('span', { className: 'dsh-brand-nametext' }, cfg.name)) : null,
        version !== '' ? (isImageValue(version)
          ? h('img', { className: 'dsh-brand-versionimg', src: ensureDataUrl(version), alt: 'version' })
          : h('span', { className: 'dsh-brand-versionbadge' }, version)) : null)
    }

    /** Hero takeover: logo + headline + badge row, with the intro line below. */
    function heroElement(cfg, size) {
      // hideHeadline='true'：logo 自带文字时不再重复显示 Hero 主标题。
      const showHeadline = cfg.headline !== '' && cfg.hideHeadline !== 'true'
      const row = h('div', { className: 'dsh-brand-hero-row' },
        markElement(cfg, size, 'hero'),
        showHeadline ? (isImageValue(cfg.headline)
          ? h('img', { className: 'dsh-brand-hero-textimg', src: ensureDataUrl(cfg.headline), alt: cfg.name || 'headline' })
          : h('span', { className: 'dsh-brand-hero-text' }, cfg.headline)) : null,
        cfg.badge !== '' ? (isImageValue(cfg.badge)
          ? h('img', { className: 'dsh-brand-hero-badgeimg', src: ensureDataUrl(cfg.badge), alt: 'badge' })
          : h('span', { className: 'dsh-brand-hero-badge' }, cfg.badge)) : null)
      return h('div', { className: 'dsh-brand-hero' }, row,
        cfg.intro !== '' ? (isImageValue(cfg.intro)
          ? h('img', { className: 'dsh-brand-hero-introimg', src: ensureDataUrl(cfg.intro), alt: 'intro' })
          : h('div', { className: 'dsh-brand-hero-intro' }, cfg.intro)) : null)
    }

    /**
     * The package stylesheet. When any hero text field is set, the stock hero
     * headline/badge spans (siblings of the slot's host span) are hidden and
     * the host span is flattened into the headline grid — no class-name
     * guessing against CSS-module hashes.
     */
    function buildCss(cfg) {
      const heroOverride = cfg.headline !== '' || cfg.badge !== '' || cfg.intro !== ''
      let css = ''
      css += '.dsh-brand-markimg{display:block;border-radius:6px;object-fit:contain;}'
      // 深/浅色双 logo：由 JS 在 <html> 上打的 dsh-brand-dark class 切换（不用媒体查询）。
      css += '.dsh-brand-markswap{display:inline-flex;align-items:center;}'
      css += '.dsh-brand-mark-dark{display:none;}'
      css += 'html.dsh-brand-dark .dsh-brand-mark-light{display:none;}'
      css += 'html.dsh-brand-dark .dsh-brand-mark-dark{display:block;}'
      css += '.dsh-brand-letter{display:inline-flex;align-items:center;justify-content:center;border-radius:6px;background:var(--dsw-alias-state-business-tertiary);color:var(--dsw-alias-label-primary-bluish);font-weight:700;line-height:1;}'
      css += '.dsh-brand-namewrap{display:inline-flex;align-items:center;gap:6px;min-width:0;overflow:hidden;white-space:nowrap;}'
      css += '.dsh-brand-nametext{font-size:17px;letter-spacing:0;white-space:nowrap;color:var(--dsw-alias-label-primary);}'
      css += '.dsh-brand-nameimg{display:block;height:22px;width:auto;object-fit:contain;}'
      css += '.dsh-brand-versionbadge{display:inline-flex;align-items:center;height:16px;padding:0 4px;border-radius:3px;color:var(--dsw-alias-label-primary-inverted);background:var(--dsw-alias-label-primary);font-family:var(--ds-font-family-code);font-size:8px;font-weight:500;line-height:16px;white-space:nowrap;flex-shrink:0;}'
      css += '.dsh-brand-versionimg{display:inline-block;height:16px;width:auto;object-fit:contain;flex-shrink:0;}'
      css += '.dsh-brand-hero{grid-column:1 / -1;display:flex;flex-direction:column;align-items:center;gap:8px;min-width:0;}'
      css += '.dsh-brand-hero-row{display:flex;align-items:center;justify-content:center;gap:10px;font-size:26px;line-height:32px;font-weight:500;color:var(--dsw-alias-label-primary);}'
      css += '.dsh-brand-hero-text{white-space:nowrap;}'
      css += '.dsh-brand-hero-textimg{display:block;height:32px;width:auto;object-fit:contain;}'
      css += '.dsh-brand-hero-badge{padding:1px 7px 0;border:1px solid var(--dsw-alias-interactive-bg-hover);border-radius:24px;background:var(--dsw-alias-state-business-tertiary);color:var(--dsw-alias-label-primary-bluish);font-family:var(--ds-font-family-code);font-size:12px;line-height:18px;font-weight:500;white-space:nowrap;}'
      css += '.dsh-brand-hero-badgeimg{display:inline-block;height:18px;width:auto;object-fit:contain;}'
      css += '.dsh-brand-hero-intro{font-size:14px;line-height:20px;font-weight:400;color:var(--dsw-alias-label-caption);text-align:center;}'
      css += '.dsh-brand-hero-introimg{display:block;max-height:20px;width:auto;object-fit:contain;margin:0 auto;}'
      css += '.dsh-brand-form{display:flex;flex-direction:column;gap:14px;max-width:480px;padding:8px 0;}'
      css += '.dsh-brand-section{margin:6px 0 -4px;font-size:12px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:var(--dsw-alias-label-caption);}'
      css += '.dsh-brand-field{display:flex;flex-direction:column;gap:6px;}'
      css += '.dsh-brand-field-label{font-size:13px;font-weight:500;color:var(--dsw-alias-label-secondary);}'
      css += '.dsh-brand-field-input{box-sizing:border-box;width:100%;height:38px;padding:6px 12px;border:1px solid var(--dsw-alias-border-l2);border-radius:10px;background:transparent;color:var(--dsw-alias-label-primary);font-size:13px;line-height:20px;outline:none;font-family:inherit;}'
      css += '.dsh-brand-field-input:focus{border-color:var(--dsw-alias-interactive-bg-hover);}'
      // 多行输入（启动屏标语）：默认 3 行高、可纵向拖拽
      css += '.dsh-brand-field-textarea{height:auto;min-height:64px;resize:vertical;line-height:18px;}'
      css += '.dsh-brand-actions{display:flex;align-items:center;gap:10px;margin-top:4px;}'
      css += '.dsh-brand-btn{min-width:88px;height:34px;padding:0 16px;border:none;border-radius:17px;font-size:13px;font-weight:500;cursor:pointer;}'
      css += '.dsh-brand-btn-primary{background:var(--dsw-alias-label-primary);color:var(--dsw-alias-label-primary-inverted);}'
      css += '.dsh-brand-btn-plain{background:transparent;border:1px solid var(--dsw-alias-border-l2);color:var(--dsw-alias-label-secondary);}'
      css += '.dsh-brand-status{font-size:12px;color:var(--dsw-alias-label-caption);}'
      css += '.dsh-brand-hint{font-size:12px;line-height:18px;color:var(--dsw-alias-label-caption);}'
      css += '.dsh-brand-field-row{flex-direction:row;align-items:center;gap:10px;}'
      css += '.dsh-brand-checkbox{width:18px;height:18px;cursor:pointer;accent-color:var(--dsw-alias-label-primary);}'
      css += '.dsh-brand-switch{position:relative;width:38px;height:22px;flex:none;}'
      css += '.dsh-brand-switch input{position:absolute;opacity:0;pointer-events:none;}'
      css += '.dsh-brand-switch-track{display:block;width:38px;height:22px;border-radius:11px;background:var(--dsw-alias-border-l2);cursor:pointer;transition:background 120ms ease;}'
      css += '.dsh-brand-switch-track::after{content:"";display:block;width:18px;height:18px;margin:2px;border-radius:50%;background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.22);transition:transform 120ms ease;}'
      css += '.dsh-brand-switch input:checked + .dsh-brand-switch-track{background:var(--dsw-alias-state-business-primary);}'
      css += '.dsh-brand-switch input:checked + .dsh-brand-switch-track::after{transform:translateX(16px);}'
      css += '.dsh-brand-scope{margin-top:-8px;padding:8px 10px;border-radius:8px;background:var(--dsw-alias-interactive-bg-hover);font-size:12px;line-height:18px;color:var(--dsw-alias-label-secondary);}'
      css += '.dsh-brand-targets{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px 12px;padding:10px;border:1px solid var(--dsw-alias-border-l2);border-radius:10px;}'
      css += '.dsh-brand-target{display:flex;align-items:center;gap:7px;min-width:0;font-size:13px;color:var(--dsw-alias-label-primary);cursor:pointer;}'
      css += '.dsh-brand-target input{width:16px;height:16px;margin:0;accent-color:var(--dsw-alias-state-business-primary);cursor:pointer;}'
      css += '.dsh-brand-target-actions{display:flex;gap:12px;margin-top:2px;}'
      css += '.dsh-brand-target-link{padding:0;border:0;background:transparent;color:var(--dsw-alias-label-secondary);font-size:12px;cursor:pointer;}'
      css += '.dsh-brand-colorrow{display:flex;align-items:center;gap:10px;}'
      css += '.dsh-brand-colorpicker{width:38px;height:38px;padding:2px;border:1px solid var(--dsw-alias-border-l2);border-radius:8px;cursor:pointer;background:transparent;flex-shrink:0;}'
      css += '.dsh-brand-colorrow .dsh-brand-field-input{flex:1;}'
      if (heroOverride) {
        css += 'span:has(.dsh-brand-hero){display:contents !important;}'
        css += 'span:has(.dsh-brand-hero) ~ span{display:none !important;}'
      }
      // Brand color, applied with surgical scope — never a global variable
      // override (that would stain every secondary label in the app). Two
      // targets only: the sidebar action icons (located by walking up from
      // our own brand-mark class, which is the stable anchor in any build),
      // and the Think disclosure row (data-variant="think"). Skin/theme
      // plugins keep full control of the global palette.
      const candidateColor = /^#[0-9a-fA-F]{3,8}$/.test(cfg.color) || /^[a-zA-Z]{3,20}$/.test(cfg.color) ? cfg.color : ''
      const brandColor = cfg.colorEnabled === 'false' ? '' : candidateColor
      const colorTargets = new Set(String(cfg.colorTargets).split(',').filter(Boolean))
      if (brandColor !== '') {
        const targets = colorTargets
        // The sidebar root owns logoRow as a direct child and the brand button
        // as logoRow's direct child. Do not add a wrapper-tolerant outer form:
        // under some skins that form also matches the app shell and would tint
        // Settings-modal navigation icons.
        const sidebarRoots = [
          'div:has(> div > button img.dsh-brand-markimg)',
          'div:has(> div > button .dsh-brand-letter)',
        ]
        const sidebarScoped = (tail) => sidebarRoots.map((root) => root + tail).join(',')
        if (targets.has('sidebar')) {
          css += sidebarScoped(' button svg') + '{color:' + brandColor + ' !important;}'
        }
        // Workspace project folder only; session/file references elsewhere keep
        // their normal semantic colors.
        if (targets.has('project')) {
          css += sidebarScoped(' [role="treeitem"][aria-expanded]>span:first-child>svg')
            + '{color:' + brandColor + ' !important;}'
        }
        if (targets.has('think')) {
          css += 'div[data-variant="think"] [class*="leading"]{color:' + brandColor + ' !important;}'
          css += 'div[data-variant="think"] span[class*="title"]{color:' + brandColor + ' !important;}'
        }
        // Preserve the Deep-diving shimmer, replacing only its blue stops.
        if (targets.has('diving')) {
          css += '[role="status"][aria-live="polite"][class*="turnStatus"]{background-image:linear-gradient(90deg,'
            + brandColor + ' 0%,' + brandColor + ' 40%,color-mix(in srgb,' + brandColor
            + ' 35%,white) 50%,' + brandColor + ' 60%,' + brandColor + ' 100%) !important;}'
        }
        // Send and Stop share one brand fill. Disabled-state opacity remains the
        // shell default; only the background color is overridden.
        if (targets.has('composer')) {
          const primaryButton = '[data-composer-card] button:has(>svg>path[d^="M8.3125"]),'
            + '[data-composer-card] button:has(>svg>rect[x="3"][y="3"][width="10"])'
          css += primaryButton + '{background:' + brandColor + ' !important;}'
        }
      }
      const iconImageCss = (button, image) => {
        const src = ensureDataUrl(image)
        css += button + '>svg{display:none !important;}'
        css += button + '::after{content:"";display:block;width:16px;height:16px;background-image:url('
          + JSON.stringify(src) + ');background-position:center;background-repeat:no-repeat;background-size:contain;}'
      }
      if (cfg.sendIcon !== '') {
        iconImageCss('[data-composer-card] button:has(>svg>path[d^="M8.3125"])', cfg.sendIcon)
      }
      if (cfg.stopIcon !== '') {
        iconImageCss('[data-composer-card] button:has(>svg>rect[x="3"][y="3"][width="10"])', cfg.stopIcon)
      }
      // Replace both the Think disclosure title and the live turn status at
      // the CSS presentation layer. This does not depend on the shell's source
      // text, so locale and skin rewrites cannot overwrite the configured copy.
      if (cfg.thinkText !== '') {
        const t = JSON.stringify(cfg.thinkText.replace(/[\r\n]+/g, ' '))
        css += 'div[data-variant="think"] span[class*="title"]{font-size:0 !important;}'
        css += 'div[data-variant="think"] span[class*="title"]::after{content:' + t + ';font-size:14px;'
          + (brandColor !== '' && colorTargets.has('think') ? 'color:' + brandColor + ';' : 'color:var(--dsw-alias-label-primary);') + '}'
        const status = '[role="status"][aria-live="polite"][class*="turnStatus"]'
        css += status + '{font-size:0 !important;}'
        css += status + '::before{content:' + t + ' !important;font:var(--dsw-font-s-strong-14);}'
        css += status + '>span{font:var(--dsw-font-xs-13) !important;font-weight:400 !important;}'
      }
      return css
    }

    /** Keep the tab title on the brand name; the shell rewrites it per session. */
    function syncTitle(cfg) {
      if (cfg.title === '') return
      const current = String(document.title || '')
      const sep = current.lastIndexOf(' — ')
      const next = sep >= 0 ? current.slice(0, sep) + ' — ' + cfg.title : cfg.title
      if (next !== current) document.title = next
    }

    /** Update the browser tab icon to the configured favicon (if any). */
    function syncFavicon(cfg) {
      // favicon 不能靠 CSS 切换：启动时按当前主题选一版（深色用 faviconDark / logoUrlDark）。
      const dark = isDarkTheme()
      const favLight = cfg.faviconResolved || cfg.favicon || cfg.logoUrlResolved || cfg.logoUrl
      const favDark = cfg.faviconDarkResolved || cfg.faviconDark || cfg.logoUrlDarkResolved || cfg.logoUrlDark
      const favSrc = (dark && favDark !== '') ? favDark : favLight
      const fav = ensureDataUrl(favSrc)
      if (fav === '') return
      let link = document.querySelector('link[rel="icon"]')
      if (link === null) {
        link = document.createElement('link')
        link.setAttribute('rel', 'icon')
        document.head.appendChild(link)
      }
      if (link.getAttribute('href') !== fav) link.setAttribute('href', fav)
    }

    /**
     * Replacement for the shipped welcome-notice onboarding step: renders
     * nothing and immediately signals completion so the onboarding
     * coordinator advances past it — no dialog, no inert root.
     */
    function SuppressedNotice(props) {
      React.useEffect(() => {
        if (props !== null && typeof props === 'object' && typeof props.complete === 'function') {
          props.complete()
        }
      }, [])
      return null
    }

    function apply(ctx) {
      const slots = ctx.slots
      const disposers = []
      let teardown = null
      let styleEl = null
      let colorGeneration = 0

      function ensureStyle(css) {
        if (styleEl === null) {
          styleEl = document.createElement('style')
          styleEl.setAttribute('data-dsh-brand', '')
          document.head.appendChild(styleEl)
        }
        styleEl.textContent = css
      }

      /** (Re)apply one config: style tag, brand slots, tab title. */
      function applyConfig(cfg) {
        if (teardown !== null) { teardown(); teardown = null }
        const next = []
        const generation = ++colorGeneration
        const displayCfg = clean(cfg)
        // A manual color always wins. When empty, use an immediate SVG color
        // fallback and then refine it asynchronously from sampled logo pixels.
        if (displayCfg.color === '') displayCfg.color = svgFallbackColor(displayCfg.logoUrlResolved || displayCfg.logoUrl)
        ensureStyle(withAccentTheme(buildCss(displayCfg), displayCfg))
        if (cfg.color === '' && (cfg.logoUrlResolved || cfg.logoUrl) !== '') {
          sampleDominantColor(cfg.logoUrlResolved || cfg.logoUrl).then((color) => {
            if (generation !== colorGeneration || color === '' || displayCfg.color === color) return
            displayCfg.color = color
            ensureStyle(withAccentTheme(buildCss(displayCfg), displayCfg))
          })
        }
        const hasMark = (cfg.logoUrlResolved || cfg.logoUrl) !== '' || cfg.logoUrlDark !== '' || cfg.logoText !== '' || cfg.name !== ''
        // hideName='true' 时也要占住 name 这个 seat，否则会露出系统默认产品名。
        const hasName = cfg.name !== '' || cfg.version !== '' || cfg.hideName === 'true'
        const heroOverride = cfg.headline !== '' || cfg.badge !== '' || cfg.intro !== ''
        // Each seat registers independently: one bad seat must not strand the rest.
        const seat = (slotName, component) => {
          try {
            next.push(slots.inject(slotName, () => slots.register({ name: slotName }, component)))
          } catch (error) {
            console.error('[dsh-brand] registration failed for ' + slotName + ':', error)
          }
        }
        if (hasMark) {
          seat('sidebar.brand.mark',
            (props) => markElement(cfg, props !== null && typeof props === 'object' && typeof props.size === 'number' ? props.size : 24))
        }
        if (hasName) {
          seat('sidebar.brand.name', () => nameElement(cfg))
        }
        if (heroOverride) {
          seat('conversation.hero.brand.mark',
            (props) => heroElement(cfg, props !== null && typeof props === 'object' && typeof props.size === 'number' ? props.size : 34))
        } else if (hasMark) {
          seat('conversation.hero.brand.mark',
            (props) => markElement(cfg, props !== null && typeof props === 'object' && typeof props.size === 'number' ? props.size : 34))
        }
        if (cfg.title !== '') {
          syncTitle(cfg)
          const timer = window.setInterval(() => syncTitle(cfg), 800)
          next.push(() => window.clearInterval(timer))
          // Intercept document.title assignments to prevent the stock
          // "DSH Local Build" flash when the shell's DocumentTitle component
          // unmounts/remounts during re-renders or HMR. Every assignment
          // containing the stock fallback is rewritten in real time — zero
          // delay, zero visible flash.
          const stockTitle = 'DSH Local Build'
          const brandTitle = cfg.title
          const desc = Object.getOwnPropertyDescriptor(Document.prototype, 'title')
          if (desc !== undefined && desc !== null && typeof desc.set === 'function') {
            Object.defineProperty(document, 'title', {
              configurable: true,
              get: desc.get,
              set(value) {
                if (typeof value === 'string' && value.includes(stockTitle)) {
                  value = value.replace(stockTitle, brandTitle)
                }
                desc.set.call(document, value)
              },
            })
            next.push(() => { try { delete document.title } catch (e) { /* harmless */ } })
          }
        }
        // Suppress only the shipped "内测声明" onboarding step. The official
        // entry is order -100, so ours must complete the same step id first.
        if (cfg.hideNotice === 'true') {
          try {
            next.push(slots.inject('settings.onboarding', () => slots.register(
              { name: 'settings.onboarding', id: 'welcome-notice', order: -1000, priority: 1 },
              SuppressedNotice,
            )))
          } catch (error) {
            console.error('[dsh-brand] registration failed for settings.onboarding/welcome-notice:', error)
          }
          // A remote browser can mount the official dialog before this client
          // package finishes loading. Close that already-mounted instance by
          // its exact accessible title; never touch API-key onboarding dialogs.
          const dismissNotice = () => {
            const dialog = document.querySelector('[role="dialog"][aria-label="内测声明"]')
            if (dialog === null) return
            const buttons = dialog.querySelectorAll('button')
            for (const button of buttons) {
              if (String(button.textContent || '').trim() === '继续') { button.click(); break }
            }
          }
          dismissNotice()
          const noticeObserver = new MutationObserver(dismissNotice)
          noticeObserver.observe(document.body, { childList: true, subtree: true })
          next.push(() => noticeObserver.disconnect())
        }
        // Replace the browser tab icon.
        if (cfg.favicon !== '') syncFavicon(cfg)
        teardown = () => {
          for (const dispose of next) {
            try { dispose() } catch (error) { /* double dispose is harmless */ }
          }
        }
      }

      /** Settings → 品牌 Branding: edit every field, save applies instantly. */
      function BrandSection() {
        const [form, setForm] = React.useState(null)
        const [status, setStatus] = React.useState('')
        const [autoColor, setAutoColor] = React.useState('#ff6b1a')
        React.useEffect(() => {
          let alive = true
          fetch(API)
            .then((res) => res.json())
            .then((cfg) => { if (alive) setForm(mergeBoot(clean(cfg))) })
            .catch(() => { if (alive) setForm(mergeBoot(blank())) })
          return () => { alive = false }
        }, [])
        const logoForColor = form === null ? '' : form.logoUrl
        React.useEffect(() => {
          let alive = true
          if (logoForColor === '') { setAutoColor('#ff6b1a'); return () => { alive = false } }
          const fallback = svgFallbackColor(logoForColor)
          if (fallback !== '') setAutoColor(fallback)
          sampleDominantColor(logoForColor).then((color) => {
            if (alive && color !== '') setAutoColor(color)
          })
          return () => { alive = false }
        }, [logoForColor])
        if (form === null) return h('div', { className: 'dsh-brand-form' }, '加载中…')
        const updateField = (key, value) => {
          const nextForm = {}
          for (const key2 of Object.keys(form)) nextForm[key2] = form[key2]
          nextForm[key] = value
          setForm(nextForm)
          setStatus('')
        }
        const section = (title) => h('div', { className: 'dsh-brand-section', key: 'sec-' + title }, title)
        const field = (key, label, placeholder) => h('div', { className: 'dsh-brand-field' },
          h('label', { className: 'dsh-brand-field-label' }, label),
          h('input', {
            className: 'dsh-brand-field-input',
            value: form[key],
            placeholder,
            onChange: (ev) => updateField(key, ev !== null && typeof ev === 'object' && ev.target ? String(ev.target.value) : ''),
          }))
        // 多行文本字段（启动屏标语）：换行可以直接敲，也可以写字面量 \n
        const textArea = (key, label, placeholder) => h('div', { className: 'dsh-brand-field' },
          h('label', { className: 'dsh-brand-field-label' }, label),
          h('textarea', {
            className: 'dsh-brand-field-input dsh-brand-field-textarea',
            value: form[key],
            placeholder,
            rows: 3,
            spellCheck: false,
            onChange: (ev) => updateField(key, ev !== null && typeof ev === 'object' && ev.target ? String(ev.target.value) : ''),
          }))
        const colorField = (key, label) => h('div', { className: 'dsh-brand-field' },
          h('label', { className: 'dsh-brand-field-label' }, label),
          h('div', { className: 'dsh-brand-colorrow' },
            h('input', {
              type: 'color',
              className: 'dsh-brand-colorpicker',
              value: /^#[0-9a-fA-F]{6}$/.test(form[key]) ? form[key] : autoColor,
              onChange: (ev) => updateField(key, ev !== null && typeof ev === 'object' && ev.target ? String(ev.target.value) : ''),
            }),
            h('input', {
              className: 'dsh-brand-field-input',
              value: form[key],
              placeholder: '如：#ff6b1a',
              onChange: (ev) => updateField(key, ev !== null && typeof ev === 'object' && ev.target ? String(ev.target.value) : ''),
            })))
        const colorTargetOptions = [
          ['sidebar', '侧边栏操作图标'],
          ['project', '项目文件夹图标'],
          ['think', 'Think / 深度思考'],
          ['diving', 'Deep diving 文字'],
          ['composer', '发送 / 停止按钮'],
        ]
        const selectedTargets = new Set(String(form.colorTargets).split(',').filter(Boolean))
        const updateTarget = (id, checked) => {
          const next = new Set(selectedTargets)
          if (checked) next.add(id)
          else next.delete(id)
          updateField('colorTargets', colorTargetOptions.map((option) => option[0]).filter((key) => next.has(key)).join(','))
        }
        const write = (cfg, pending, done) => {
          // Auto-convert raw SVG markup to data URLs before saving.
          const imageFields = ['logoUrl', 'logoUrlDark', 'favicon', 'faviconDark', 'sendIcon', 'stopIcon', 'name', 'version', 'headline', 'badge', 'intro']
          const payload = {}
          for (const key of KEYS) {
            const isLocalPath = /^\.{0,2}[\\/]/.test(String(cfg[key] || ''))
          payload[key] = (imageFields.indexOf(key) !== -1 && !isLocalPath) ? ensureDataUrl(cfg[key]) : cfg[key]
          }
          setStatus(pending)
          fetch(API, {
            method: 'PUT',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify(payload),
          })
            .then((res) => {
              if (!res.ok) throw new Error('HTTP ' + res.status)
              return res.json()
            })
            .then((saved) => {
              const normalized = clean(saved)
              // A stale Host filters fields it does not know. Surface that
              // explicitly instead of silently snapping the checkboxes back.
              const hasSaved = (key) => saved !== null && typeof saved === 'object' && Object.prototype.hasOwnProperty.call(saved, key)
              if (!hasSaved('colorTargets') || !hasSaved('useDshVersion')
                || normalized.colorTargets !== cfg.colorTargets
                || normalized.useDshVersion !== cfg.useDshVersion) {
                throw new Error('当前 Host 未加载新版配置字段，请重启 dsh web 后再保存')
              }
              applyConfig(normalized)
              setForm(normalized)
              setStatus(done)
            })
            .catch((error) => { setStatus('操作失败：' + String(error)) })
        }
        return h('div', { className: 'dsh-brand-form' },
          h('div', { className: 'dsh-brand-hint' },
            h('div', null, '配置保存在 $DSH_HOME/dsh-brand.json，可随分发直接带走。'),
            h('div', null, '图片类字段支持：网络 URL、Data URL、原始 SVG 代码，或相对 $DSH_HOME 的本地路径（如 ./brand/logo.svg，文件放 dsh-home\\brand\\ 下）。'),
            h('div', null, '深色主题商标留空＝跟随浅色主题商标；侧栏 / Hero 商标高度留空＝默认 45px / 128px。'),
            h('div', null, 'Hero 三项（主标题 / 徽标 / 简介）任一填写即接管新会话页头部，全部留空显示官方默认。'),
            h('div', null, '品牌主色留空时从商标图片自动取色；暗色主题主色留空＝跟随主色。主色只作用于侧栏图标、项目文件夹、Think / Deep diving 与发送停止按钮，不修改全局配色。'),
            h('div', null, '桌面启动屏（DshDesktop）：商标、品牌主色、「产品名称」「隐藏产品名文字」「隐藏 Hero 主标题」「浏览器标签页标题」与「桌面启动屏」分组里的两个字段同时作用于 DshDesktop 的原生启动画面（分别对应字标 / 字标显隐 / 标语显隐 / 窗口标题）—— 这些字段需要「重启 DshDesktop」后生效。')),
          section('侧栏'),
          field('name', '产品名称（侧栏与桌面启动屏字标，文字 / 图片 URL / SVG）', '如：Acme Harness 或 data:image/svg+xml,…'),
          field('version', '版本徽标（文字 / 图片 URL / SVG）', '留空时可显示 DSH 构建版本；如：v1.0.0'),
          h('div', { className: 'dsh-brand-field dsh-brand-field-row' },
            h('label', { className: 'dsh-brand-field-label' },
              '版本留空时显示 DSH 构建版本' + (form.dshBuildVersion !== '' ? '（' + form.dshBuildVersion + '）' : '（当前不可用）')),
            h('label', { className: 'dsh-brand-switch' },
              h('input', {
                type: 'checkbox',
                checked: form.useDshVersion !== 'false',
                onChange: (ev) => updateField('useDshVersion', ev !== null && typeof ev === 'object' && ev.target && ev.target.checked ? 'true' : 'false'),
              }),
              h('span', { className: 'dsh-brand-switch-track' }))),
          section('Hero（新会话页头部）'),
          field('headline', 'Hero 主标题（文字 / 图片 URL / SVG）', '如：探索未至之境 或艺术字 SVG'),
          field('badge', 'Hero 徽标（文字 / 图片 URL / SVG）', '如：预览版 或 badge 图片 URL'),
          field('intro', '一句话简介（文字 / 图片 URL / SVG）', '如：面向内部的智能体工作台'),
          section('商标与图标'),
          field('logoUrl', '商标（文字 / Emoji / 图片 URL / SVG / 相对路径）', '如：🐳 或 https://… 或 data:image/svg+xml,… 或 ./brand/logo.svg'),
          field('logoUrlDark', '深色主题商标（可留空＝跟随上面）', '如：./brand/logo-dark.svg（相对 $DSH_HOME，即 dsh-home 目录）'),
          field('favicon', '浏览器标签页图标（URL / Data URL / SVG / 相对路径）', '如：./brand/logo.svg 或 https://…/favicon.svg'),
          field('faviconDark', '深色主题标签页图标（可留空）', '如：./brand/logo-dark.svg'),
          field('title', '浏览器标签页标题', '如：Acme Harness'),
          field('sendIcon', '发送按钮图标（图片 URL / Data URL / SVG）', '留空使用系统箭头；如：https://…/send.svg'),
          field('stopIcon', '停止按钮图标（图片 URL / Data URL / SVG）', '留空使用系统停止方块；如：https://…/stop.svg'),
          field('thinkText', '思考状态文字（Think / Deep diving）', '如：思考中；留空使用系统文字'),
          section('尺寸与显示'),
          field('markHeight', '侧栏商标高度 px（留空＝30）', '如：30'),
          field('heroMarkHeight', 'Hero 商标高度 px（留空＝60）', '如：60'),
          // 桌面启动屏（DshDesktop）：桌面壳启动时直接读同一份 $DSH_HOME/dsh-brand.json，
          // 这里只管写文件；改完要重启 DshDesktop 才会看到效果（不做文件监听）。
          section('桌面启动屏（DshDesktop）'),
          textArea('bootSlogan', '启动屏标语（留空＝内置标语）', '多行文本；换行直接回车，或写字面量 \\n；留空使用内置标语'),
          field('desktopLogoHeight', '启动屏标志高度 px（留空＝96）', '如：96（logoUrl 图片与内置鲸鱼都适用）'),
          h('div', { className: 'dsh-brand-field dsh-brand-field-row' },
            h('label', { className: 'dsh-brand-field-label' }, '隐藏产品名文字（logo 自带文字时；桌面启动屏同步隐藏字标）'),
            h('input', {
              type: 'checkbox',
              className: 'dsh-brand-checkbox',
              checked: form.hideName === 'true',
              onChange: (ev) => updateField('hideName', ev !== null && typeof ev === 'object' && ev.target && ev.target.checked ? 'true' : ''),
            })),
          h('div', { className: 'dsh-brand-field dsh-brand-field-row' },
            h('label', { className: 'dsh-brand-field-label' }, '隐藏 Hero 主标题（logo 自带文字时；桌面启动屏隐藏标语）'),
            h('input', {
              type: 'checkbox',
              className: 'dsh-brand-checkbox',
              checked: form.hideHeadline === 'true',
              onChange: (ev) => updateField('hideHeadline', ev !== null && typeof ev === 'object' && ev.target && ev.target.checked ? 'true' : ''),
            })),
          section('品牌主色'),
          h('div', { className: 'dsh-brand-field dsh-brand-field-row' },
            h('label', { className: 'dsh-brand-field-label' }, '启用品牌主色'),
            h('label', { className: 'dsh-brand-switch' },
              h('input', {
                type: 'checkbox',
                checked: form.colorEnabled !== 'false',
                onChange: (ev) => updateField('colorEnabled', ev !== null && typeof ev === 'object' && ev.target && ev.target.checked ? 'true' : 'false'),
              }),
              h('span', { className: 'dsh-brand-switch-track' }))),
          colorField('color', '品牌主色（留空则从商标自动取色）'),
          colorField('colorDark', '暗色主题主色（留空＝跟随上面的主色；暗底上看不清就填 #FFFFFF）'),
          h('div', { className: 'dsh-brand-field' },
            h('label', { className: 'dsh-brand-field-label' }, '品牌主色作用范围'),
            h('div', { className: 'dsh-brand-targets' },
              ...colorTargetOptions.map((option) => h('label', { className: 'dsh-brand-target', key: option[0] },
                h('input', {
                  type: 'checkbox',
                  checked: selectedTargets.has(option[0]),
                  onChange: (ev) => updateTarget(option[0], ev !== null && typeof ev === 'object' && ev.target && ev.target.checked),
                }),
                h('span', null, option[1])))),
            h('div', { className: 'dsh-brand-target-actions' },
              h('button', { type: 'button', className: 'dsh-brand-target-link', onClick: () => updateField('colorTargets', ALL_COLOR_TARGETS) }, '全选'),
              h('button', { type: 'button', className: 'dsh-brand-target-link', onClick: () => updateField('colorTargets', '') }, '全部取消'))),
          h('div', { className: 'dsh-brand-scope' },
            '默认全部勾选，效果与当前版本一致。可取消不希望使用品牌色的 UI；总开关关闭时全部恢复系统原配色，选择记录仍会保留。'),
          section('其它'),
          h('div', { className: 'dsh-brand-field dsh-brand-field-row' },
            h('label', { className: 'dsh-brand-field-label' }, '隐藏 DSH 内测声明弹窗'),
            h('input', {
              type: 'checkbox',
              className: 'dsh-brand-checkbox',
              checked: form.hideNotice === 'true',
              onChange: (ev) => updateField('hideNotice', ev !== null && typeof ev === 'object' && ev.target && ev.target.checked ? 'true' : ''),
            })),
          h('div', { className: 'dsh-brand-actions' },
            h('button', {
              type: 'button',
              className: 'dsh-brand-btn dsh-brand-btn-primary',
              onClick: () => write(form, '保存中…', '已保存并应用 ✔'),
            }, '保存并应用'),
            h('button', {
              type: 'button',
              className: 'dsh-brand-btn dsh-brand-btn-plain',
              onClick: () => { const empty = blank(); setForm(empty); write(empty, '恢复中…', '已恢复官方默认 ✔') },
            }, '恢复默认'),
            status !== '' ? h('span', { className: 'dsh-brand-status' }, status) : null))
      }

      disposers.push(slots.inject('settings.section', () => slots.register(
        { name: 'settings.section', id: 'brand', order: 90, label: '品牌 Branding' },
        BrandSection,
      )))

      // Boot synchronously from the config the host stamped into the HTML —
      // branding must be in place before first paint, not after a fetch.
      let appliedJson = ''
      const boot = typeof window === 'object' && window !== null ? window.__DSH_BRAND__ : undefined
      if (boot !== null && typeof boot === 'object') {
        const cfg = clean(boot)
        appliedJson = JSON.stringify(cfg)
        applyConfig(cfg)
      }

      // Then reconcile with the live file (covers saves made after this HTML
      // was served); a same-value response is a no-op.
      fetch(API)
        .then((res) => res.json())
        .then((raw) => {
          const cfg = clean(raw)
          const json = JSON.stringify(cfg)
          if (json !== appliedJson) {
            appliedJson = json
            applyConfig(cfg)
          }
        })
        .catch(() => { /* the boot-stamped config (or stock branding) stays */ })

      return () => {
        if (teardown !== null) { teardown(); teardown = null }
        if (styleEl !== null) { styleEl.remove(); styleEl = null }
        for (const dispose of disposers) {
          try { dispose() } catch (error) { /* double dispose is harmless */ }
        }
      }
    }

    return { name: 'dsh-brand', inject: ['slots'], apply }
  },
})
