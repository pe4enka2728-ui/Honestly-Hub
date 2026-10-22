# Логотип Honestly TUN

`logo-original.png` — исходное изображение пользователя, 551 × 444.
`logo.png` — тот же белый силуэт с прозрачными чёрными участками.
`HonestlyTUN.ico` — иконка с размерами 16, 24, 32, 48, 64, 128, 256.

Две попытки удаления фона встроенным imagegen добавили артефакты и не использованы в приложении. После явного разрешения пользователя фон удалён программно, без генерации или перерисовки: исходные полностью белые пиксели сохранены, чёрные стали прозрачными, яркость краевых пикселей перенесена в альфа-канал. Это сохраняет сглаживание исходного силуэта. Проверены 45 083 исходных белых пикселя и 182 989 полностью прозрачных пикселей.

Финальный способ: программная обработка исходного PNG, затем упаковка в ICO; промпт для финального файла не применялся.

Последний отклонённый промпт imagegen:

> Background extraction only, for an application icon. The reference is a CLEAN FLAT WHITE VECTOR-LIKE SILHOUETTE on black. Produce the exact same silhouette, complete, centered, with all black replaced by actual transparent alpha. Keep the white shapes SOLID PURE WHITE with smooth clean edges. Absolutely no noise, no speckles, no splatter, no distressed texture, no extra marks, no shadow, no outlines, no gradients. Preserve the original head silhouette, curved thin upper strand, left and right segmented ribbon shapes and lower hooks exactly in their original proportions and positions. Preserve every negative space as fully transparent. No text or new elements. This is a precision cutout, not a reinterpretation or redesign.
