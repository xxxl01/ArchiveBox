# Application icon

Artwork supplied by the ArchiveBox owner:
https://s41.ax1x.com/2026/11/12/pnIeUoD.png

`ArchiveBox.png` is the original 1254 x 1254 PNG, including its transparency, copied without changes.

Original PNG SHA-256: `d3b9197374d4e55699edef3169958ff1af97e20945accaf38bf71e7afb959717`.

`ArchiveBox.ico` is a Windows icon generated from the original PNG with Pillow:

```python
from PIL import Image
Image.open("ArchiveBox.png").save("ArchiveBox.ico", format="ICO",
    sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)])
```

Both assets are embedded in the application. Building from the repository does not require Pillow or network access to the image host.
