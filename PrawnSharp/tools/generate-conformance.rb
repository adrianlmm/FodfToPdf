# frozen_string_literal: true

# Genera los PDFs de referencia de Prawn para el corpus de conformidad.
# Uso:
#   ruby generate-conformance.rb [corpus.json] [carpetaSalida]
#
# Requiere el checkout de Prawn; PRAWN_SOURCES puede apuntar a otro checkout.

require 'json'
require 'fileutils'

root = ENV['PRAWN_SOURCES'] || 'C:/projects/ruby/pdf/prawn_sources'
%w[prawn pdf-core ttfunk].each do |repo|
  lib = File.join(root, repo, 'lib')
  raise "No existe #{lib}" unless Dir.exist?(lib)
  $LOAD_PATH.unshift(lib)
end

require 'prawn'

base = File.expand_path('..', __dir__)
corpus_path = ARGV[0] || File.join(base, 'conformance', 'corpus.json')
output_dir = ARGV[1] || File.join(base, 'conformance', 'reference')
FileUtils.mkdir_p(output_dir)

corpus = JSON.parse(File.read(corpus_path))
items = corpus.fetch('items')

items.each do |item|
  path = File.join(output_dir, "#{item.fetch('id')}.pdf")
  width = item.fetch('width').to_f
  font = item.fetch('font')
  size = item.fetch('size').to_f
  text = item.fetch('text')
  inline = item.fetch('format', 'plain') == 'inline'

  Prawn::Document.generate(
    path,
    page_size: [width + 20, 5000],
    margin: 10
  ) do
    font_path = item['font_path'] || item['fontPath']
    if font_path && !font_path.to_s.empty?
      font(File.expand_path(font_path, base), size: size)
    else
      font(font, size: size)
    end

    bounding_box([0, cursor], width: width) do
      if inline
        text(text, inline_format: true)
      else
        text(text)
      end
    end
  end
end

puts "Referencias: #{items.length} PDFs en #{output_dir}"
