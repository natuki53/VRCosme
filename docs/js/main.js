// Navigation active highlight is intentionally disabled.

(() => {
  const metric = document.querySelector("[data-release-downloads]");
  if (!metric) return;

  const output = metric.querySelector("[data-download-count]");
  if (!output) return;

  const owner = metric.dataset.owner;
  const repo = metric.dataset.repo;
  const manualExtraRaw = metric.dataset.manualExtra;

  const parseManualNumber = (value) => {
    if (value === undefined || value === null) return null;
    const normalized = String(value).replace(/,/g, "").trim();
    if (!normalized) return null;
    const number = Number(normalized);
    return Number.isFinite(number) ? number : null;
  };

  const manualExtra = parseManualNumber(manualExtraRaw) ?? 0;

  if (!owner || !repo) {
    output.textContent = "設定がありません";
    return;
  }

  const cacheKey = `gh-release-downloads:${owner}/${repo}`;
  const cacheTtlMs = 6 * 60 * 60 * 1000;

  const readCache = () => {
    try {
      const raw = localStorage.getItem(cacheKey);
      if (!raw) return null;
      const parsed = JSON.parse(raw);
      if (!parsed || typeof parsed !== "object") return null;
      if (!Number.isFinite(parsed.total) || !Number.isFinite(parsed.savedAt)) return null;
      return parsed;
    } catch (error) {
      return null;
    }
  };

  const writeCache = (total) => {
    try {
      localStorage.setItem(
        cacheKey,
        JSON.stringify({
          total,
          savedAt: Date.now(),
        })
      );
    } catch (error) {
      // Ignore storage failures (private mode, quota limits).
    }
  };

  const formatCount = (total) => `${total.toLocaleString("ja-JP")} 回`;

  const extractNextUrl = (linkHeader) => {
    if (!linkHeader) return null;
    const links = linkHeader.split(",").map((part) => part.trim());
    for (const link of links) {
      const match = link.match(/<([^>]+)>;\s*rel="([^"]+)"/);
      if (match && match[2] === "next") {
        return match[1];
      }
    }
    return null;
  };

  const fetchReleaseDownloads = async () => {
    let url = `https://api.github.com/repos/${owner}/${repo}/releases?per_page=100`;
    let total = 0;
    let guard = 0;

    while (url && guard < 20) {
      guard += 1;
      const response = await fetch(url, {
        headers: {
          Accept: "application/vnd.github+json",
        },
      });

      if (!response.ok) {
        throw new Error(`GitHub API error: ${response.status}`);
      }

      const releases = await response.json();
      if (!Array.isArray(releases)) break;

      for (const release of releases) {
        if (!release || !Array.isArray(release.assets)) continue;
        for (const asset of release.assets) {
          total += Number(asset.download_count) || 0;
        }
      }

      const nextUrl = extractNextUrl(response.headers.get("Link"));
      url = nextUrl;
    }

    return total;
  };

  const updateDownloadMetric = async () => {
    const cached = readCache();
    if (cached && Date.now() - cached.savedAt < cacheTtlMs) {
      output.textContent = formatCount(cached.total + manualExtra);
      return;
    }

    try {
      const total = await fetchReleaseDownloads();
      output.textContent = formatCount(total + manualExtra);
      writeCache(total);
    } catch (error) {
      output.textContent = "取得できませんでした";
      console.warn("[VRCosme] Failed to fetch release downloads", error);
    }
  };

  updateDownloadMetric();
})();
