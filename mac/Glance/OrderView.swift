import SwiftUI

@MainActor
final class OrderModel: ObservableObject {
    unowned let app: AppDelegate
    @Published var items: [(entry: String, label: String)]
    @Published var ticked: Set<String>
    @Published var selected: String?
    @Published var status = ""
    var changed = false
    var close: () -> Void = {}

    init(app: AppDelegate) {
        self.app = app
        let options = app.settings.options()
        let chain = app.settings.chain(options)
        // the chain first, in its order, then the ones left out
        items = chain.compactMap { entry in options.first { $0.entry == entry } } + options.filter { !chain.contains($0.entry) }
        ticked = Set(chain)
        selected = items.first?.entry
    }

    func binding(_ entry: String) -> Binding<Bool> {
        Binding(get: { self.ticked.contains(entry) }, set: { on in
            if on {
                self.ticked.insert(entry)
            } else {
                self.ticked.remove(entry)
            }
            self.selected = entry
        })
    }

    func shift(_ by: Int) {
        guard let at = items.firstIndex(where: { $0.entry == selected }), items.indices.contains(at + by) else { return }
        items.swapAt(at, at + by)
    }

    func save() {
        let order = items.map(\.entry).filter(ticked.contains)
        if order.isEmpty {
            status = L("TickOne")
            return
        }
        app.settings.order = order
        changed = true
        close()
    }
}

struct OrderView: View {
    @ObservedObject var m: OrderModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("OrderNote"))
                .fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .top, spacing: 12) {
                List(m.items, id: \.entry, selection: $m.selected) { item in
                    Toggle(item.label, isOn: m.binding(item.entry))
                        .tag(item.entry)
                }
                .frame(height: 170)
                VStack {
                    Button(L("Up")) { m.shift(-1) }
                    Button(L("Down")) { m.shift(1) }
                }
            }
            Text(m.status)
                .foregroundStyle(.red)
                .frame(maxWidth: .infinity, minHeight: 20, alignment: .leading)
            HStack {
                Spacer()
                Button(L("Cancel")) { m.close() }
                    .keyboardShortcut(.cancelAction)
                Button(L("Save")) { m.save() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 420)
    }
}
