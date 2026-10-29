$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 从完整皮肤裁出正脸及透明帽子层，以整数最近邻放大；输出无底色无边框头像，保留源皮肤。
function Export-SkinAvatar([string]$name) {
    $assets = Join-Path $PSScriptRoot '../src/NyxLauncher/Assets'
    $skin = [System.Drawing.Bitmap]::new((Join-Path $assets "$name.png"))
    try {
        if ($skin.Width % 64 -ne 0 -or $skin.Height -lt $skin.Width / 2) { throw '皮肤尺寸不符合 Minecraft 布局。' }
        $scale = [int]($skin.Width / 64)
        $avatar = [System.Drawing.Bitmap]::new(64, 64)
        $graphics = [System.Drawing.Graphics]::FromImage($avatar)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $target = [System.Drawing.Rectangle]::new(0, 0, 64, 64)
            foreach ($x in @(8, 40)) {
                $source = [System.Drawing.Rectangle]::new($x * $scale, 8 * $scale, 8 * $scale, 8 * $scale)
                $graphics.DrawImage($skin, $target, $source, [System.Drawing.GraphicsUnit]::Pixel)
            }
            $avatar.Save((Join-Path $assets "$name-avatar.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $avatar.Dispose() }
    } finally { $skin.Dispose() }
}
Export-SkinAvatar 'steve'
Export-SkinAvatar 'alex'
