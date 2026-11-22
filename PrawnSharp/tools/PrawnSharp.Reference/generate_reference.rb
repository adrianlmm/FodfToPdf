# frozen_string_literal: true

# Genera un PDF de referencia con el checkout local de Prawn y muestra el ancho de
# "Hola mundo" a 12 pt. Uso:
#
#   ruby generate_reference.rb [salida.pdf]
#
# PRAWN_SOURCES puede apuntar a otro checkout (por omisión C:/projects/ruby/pdf/prawn_sources).

root = ENV['PRAWN_SOURCES'] || 'C:/projects/ruby/pdf/prawn_sources'
output = ARGV[0] || 'prawn_reference.pdf'

%w[prawn pdf-core ttfunk].each do |repo|
  lib = File.join(root, repo, 'lib')
  raise "No existe #{lib}" unless Dir.exist?(lib)
  $LOAD_PATH.unshift(lib)
end

require 'prawn'

doc = Prawn::Document.new(page_size: 'A4', margin: 32)
doc.font('Helvetica', size: 12)

width = doc.width_of('Hola mundo', size: 12)
doc.text('Hola mundo')
doc.move_down(12)
doc.text("width_of('Hola mundo', size: 12) = #{width}")
doc.render_file(output)

puts "OK #{output}"
puts "width_of('Hola mundo', size: 12) = #{width}"
